import AppKit
import ClipSyncCore
import SwiftUI

/// 온보딩 입력 상태와 동작 (spec 6.1, 3.2). passphrase는 새로 생성하거나 첫 기기의 것을 그대로 입력한다.
@MainActor
@Observable
final class OnboardingModel {
    enum Mode { case generate, existing }
    var mode: Mode = .generate
    var serverURLText = Settings.shared.serverURL?.absoluteString ?? ""
    var passphraseText = ""
    var generated = ""
    var status = ""
    var busy = false
    var done = false
    var onFinished: () -> Void = {}

    init() { regenerate() }

    func regenerate() { generated = (try? generatePassphrase()) ?? "" }

    var passphrase: String { mode == .generate ? generated : passphraseText }

    var vaultId: String? { (try? deriveKeys(passphrase: passphrase))?.vaultId }

    /// 연결 테스트 후 저장. 서버가 이 vault를 모르면(401) 저장하지 않는다.
    func saveAndConnect() async {
        guard let url = ServerURLInput.parse(serverURLText) else {
            status = "서버 URL은 https://… 형식이어야 합니다 (로컬 개발은 http://localhost 만 허용)."
            return
        }
        guard isAcceptableCustomPassphrase(passphrase) else {
            status = "passphrase는 공백 정리 후 24자 이상이어야 합니다."
            return
        }
        busy = true
        defer { busy = false }
        do {
            let keys = try deriveKeys(passphrase: passphrase)
            let client = try ServerClient(baseURL: url, keys: keys, deviceId: Settings.shared.deviceId)
            _ = try await client.list(since: 0)
            try KeychainStore.save(passphrase: passphrase)
            Settings.shared.serverURL = url
            Settings.shared.lastSeq = nil   // 첫 연결: hello.seq로 초기화 (과거 항목 자동 적용 방지)
            status = "연결 성공. 저장했습니다."
            done = true
            onFinished()
        } catch ServerError.http(let s, _) where s == 401 {
            status = "인증 실패(401): 서버의 VAULT_ID가 이 passphrase와 다릅니다. 새 passphrase라면 아래 vault_id를 서버에 등록해야 합니다."
        } catch {
            status = "연결 실패: \(error)"
        }
    }
}

struct OnboardingView: View {
    @Bindable var model: OnboardingModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("ClipSync 설정").font(.title2.bold())

            TextField("서버 URL (예: https://clipsync.example.workers.dev)", text: $model.serverURLText)
                .textFieldStyle(.roundedBorder)

            Picker("", selection: $model.mode) {
                Text("새 passphrase 만들기").tag(OnboardingModel.Mode.generate)
                Text("기존 passphrase 입력").tag(OnboardingModel.Mode.existing)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            if model.mode == .generate {
                Text(model.generated).font(.system(.body, design: .monospaced)).textSelection(.enabled)
                    .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
                HStack {
                    Button("다시 생성") { model.regenerate() }
                    Button("복사") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(model.generated, forType: .string)
                    }
                }
                Text("이 passphrase를 다른 기기에 그대로 입력해야 합니다. 분실하면 복구할 수 없습니다.")
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                SecureField("passphrase", text: $model.passphraseText).textFieldStyle(.roundedBorder)
            }

            if let id = model.vaultId {
                Text("vault_id: \(id)").font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                Text("새 vault라면 서버에서 `wrangler secret put VAULT_ID` 로 위 값을 등록하세요.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            if !model.status.isEmpty { Text(model.status).font(.callout).foregroundStyle(model.done ? .green : .orange) }

            HStack {
                Spacer()
                Button(model.busy ? "연결 중…" : "연결 테스트 후 저장") { Task { await model.saveAndConnect() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.busy)
            }
        }
        .padding(20)
        .frame(width: 520)
    }
}

@MainActor
final class OnboardingWindowController {
    private var window: NSWindow?

    func show(onFinished: @escaping () -> Void) {
        if let w = window { w.makeKeyAndOrderFront(nil); NSApp.activate(); return }
        let model = OnboardingModel()
        model.onFinished = { [weak self] in
            onFinished()
            self?.window?.close()
        }
        let w = NSWindow(contentViewController: NSHostingController(rootView: OnboardingView(model: model)))
        w.title = "ClipSync 설정"
        w.styleMask = [.titled, .closable]
        w.isReleasedWhenClosed = false
        w.center()
        window = w
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.window = nil }
        }
        NSApp.activate()
        w.makeKeyAndOrderFront(nil)
    }
}
