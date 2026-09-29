import AppKit
import ClipSyncCore
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private let onboarding = OnboardingWindowController()
    private let engine = SyncEngine()
    private let settings = Settings.shared

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "ClipSync")
        engine.onChange = { [weak self] in self?.updateIcon() }
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        // 슬립에서 깨어나면 즉시 재연결 (6.5)
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in Log.info("system wake → restart"); self?.engine.restart() }
        }

        if configuredVault() == nil { showOnboarding() } else { engine.start() }
        updateIcon()
    }

    // MARK: 상태

    private func configuredVault() -> (url: URL, vaultId: String)? {
        guard let url = settings.serverURL, let pass = try? KeychainStore.load(),
              let keys = try? deriveKeys(passphrase: pass) else { return nil }
        return (url, keys.vaultId)
    }

    private func updateIcon() {
        let name: String
        switch engine.connection {
        case .connected: name = engine.isPaused ? "pause.circle" : "doc.on.clipboard.fill"
        case .connecting: name = "arrow.triangle.2.circlepath"
        case .offline: name = "exclamationmark.triangle"
        case .off: name = "doc.on.clipboard"
        }
        statusItem.button?.image = NSImage(systemSymbolName: name, accessibilityDescription: "ClipSync")
    }

    private func showOnboarding() {
        onboarding.show { [weak self] in self?.engine.start() }
    }

    // MARK: 메뉴 (열릴 때마다 새로 만든다)

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        guard let v = configuredVault() else {
            menu.addItem(label("설정 필요"))
            menu.addItem(action("설정…", #selector(openOnboarding)))
            menu.addItem(.separator())
            menu.addItem(NSMenuItem(title: "종료", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
            return
        }

        let dot: String
        switch engine.connection {
        case .connected: dot = "● 연결됨"
        case .connecting: dot = "○ 연결 중…"
        case .offline: dot = "⚠︎ 오프라인 (재연결 중)"
        case .off: dot = "○ 꺼짐"
        }
        menu.addItem(label("\(dot) · \(v.url.host ?? "")"))
        if let m = engine.lastMessage { menu.addItem(label("⚠︎ \(m)")) }
        if PasteboardIO.accessNeedsAttention {
            menu.addItem(action("권한 필요: 시스템 설정에서 클립보드 접근 허용…", #selector(openPrivacySettings)))
        }

        menu.addItem(toggle("동기화", on: settings.syncEnabled, #selector(toggleSync)))
        let pause = NSMenuItem(title: engine.isPaused ? "일시정지 중 (\(pauseRemaining()))" : "일시정지", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        sub.addItem(action("15분", #selector(pause15)))
        sub.addItem(action("1시간", #selector(pause60)))
        if engine.isPaused { sub.addItem(action("해제", #selector(resume))) }
        pause.submenu = sub
        menu.addItem(pause)
        menu.addItem(toggle("보내기", on: settings.sendEnabled, #selector(toggleSend)))
        menu.addItem(toggle("받기", on: settings.receiveEnabled, #selector(toggleReceive)))
        menu.addItem(.separator())

        let recent = NSMenuItem(title: "최근 항목", action: nil, keyEquivalent: "")
        let rsub = NSMenu()
        if engine.recent.isEmpty { rsub.addItem(label("없음")) }
        for r in engine.recent {
            let title = recentTitle(r)
            let mi = NSMenuItem(title: title, action: r.purged ? nil : #selector(restoreItem(_:)), keyEquivalent: "")
            mi.target = self
            mi.representedObject = r.id
            mi.isEnabled = !r.purged
            rsub.addItem(mi)
        }
        recent.submenu = rsub
        menu.addItem(recent)
        menu.addItem(.separator())

        menu.addItem(toggle("로그인 시 자동 시작", on: SMAppService.mainApp.status == .enabled, #selector(toggleLogin)))
        menu.addItem(action("설정 다시 열기…", #selector(openOnboarding)))
        menu.addItem(NSMenuItem(title: "종료", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
    }

    private func recentTitle(_ r: SyncEngine.RecentItem) -> String {
        let time = Date(timeIntervalSince1970: Double(r.createdAt) / 1000).formatted(date: .omitted, time: .shortened)
        let src = r.deviceId == settings.deviceId ? "이 Mac" : "기기 \(r.deviceId.prefix(4))"
        let kind = r.kinds.contains("html") ? "HTML" : "텍스트"
        let text = r.preview.replacingOccurrences(of: "\n", with: " ")
        let short = text.count > 40 ? String(text.prefix(40)) + "…" : text
        return "\(time) · \(kind) · \(src)\(r.purged ? " · 서버에서 삭제됨" : "") \(short)"
    }

    private func pauseRemaining() -> String {
        guard let until = settings.pausedUntil else { return "" }
        let m = max(1, Int(until.timeIntervalSinceNow / 60) + 1)
        return "\(m)분 남음"
    }

    private func label(_ t: String) -> NSMenuItem {
        let i = NSMenuItem(title: t, action: nil, keyEquivalent: "")
        i.isEnabled = false
        return i
    }

    private func action(_ t: String, _ sel: Selector) -> NSMenuItem {
        let i = NSMenuItem(title: t, action: sel, keyEquivalent: "")
        i.target = self
        return i
    }

    private func toggle(_ t: String, on: Bool, _ sel: Selector) -> NSMenuItem {
        let i = action(t, sel)
        i.state = on ? .on : .off
        return i
    }

    // MARK: 동작

    @objc private func openOnboarding() { showOnboarding() }
    @objc private func openPrivacySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Pasteboard")!)
    }
    @objc private func toggleSync() {
        settings.syncEnabled.toggle()
        if settings.syncEnabled { engine.start() } else { engine.stop() }   // 전체 off: WebSocket 종료, 감시 중지 (6.1)
        updateIcon()
    }
    @objc private func toggleSend() { settings.sendEnabled.toggle() }
    @objc private func toggleReceive() { settings.receiveEnabled.toggle() }
    @objc private func pause15() { settings.pausedUntil = Date().addingTimeInterval(15 * 60); updateIcon() }
    @objc private func pause60() { settings.pausedUntil = Date().addingTimeInterval(60 * 60); updateIcon() }
    @objc private func resume() { settings.pausedUntil = nil; updateIcon() }
    @objc private func restoreItem(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String, let item = engine.recent.first(where: { $0.id == id }) else { return }
        engine.restore(item)
    }
    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() } else { try SMAppService.mainApp.register() }
        } catch {
            NSLog("login item: \(error)")
        }
    }
}
