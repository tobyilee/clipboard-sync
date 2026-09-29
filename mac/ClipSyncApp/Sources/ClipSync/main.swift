import AppKit
import Foundation

// `ClipSync --selftest-keychain`: 서명된 번들에서 Keychain 저장/조회/삭제가 프롬프트 없이 되는지 확인하고 종료한다.
if CommandLine.arguments.contains("--selftest-keychain") {
    KeychainStore.account = "selftest"   // 실제 사용자 항목(account=passphrase)과 분리
    do {
        try KeychainStore.save(passphrase: "selftest passphrase value")
        let back = try KeychainStore.load()
        try KeychainStore.delete()
        let gone = try KeychainStore.load()
        print(back == "selftest passphrase value" && gone == nil ? "keychain: OK" : "keychain: MISMATCH")
        exit(back == "selftest passphrase value" && gone == nil ? 0 : 1)
    } catch {
        print("keychain: FAILED \(error)")
        exit(1)
    }
}

// LSUIElement 메뉴바 앱. 상태/동기화 로직은 AppDelegate에 붙인다.
MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
