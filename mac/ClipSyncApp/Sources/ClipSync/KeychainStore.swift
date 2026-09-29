import Foundation
import Security

/// passphrase를 로그인 키체인의 generic password로 저장한다 (access group/데이터 보호 키체인 없음, D-38).
/// 접근 권한은 앱의 코드 서명 요구사항에 묶이므로 같은 서명 ID로 재빌드해도 유지된다.
enum KeychainStore {
    static let service = "com.tobylee.clipsync"
    static let productionAccount = "passphrase"
    /// 자체 테스트(`--selftest-keychain`)는 실제 passphrase 항목을 건드리지 않도록 다른 account를 쓴다.
    nonisolated(unsafe) static var account = productionAccount

    enum KeychainError: Error { case status(OSStatus) }

    private static var baseQuery: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }

    static func save(passphrase: String) throws {
        let data = Data(passphrase.utf8)
        let status = SecItemUpdate(baseQuery as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var add = baseQuery
            add[kSecValueData as String] = data
            let s = SecItemAdd(add as CFDictionary, nil)
            guard s == errSecSuccess else { throw KeychainError.status(s) }
        } else if status != errSecSuccess {
            throw KeychainError.status(status)
        }
    }

    static func load() throws -> String? {
        var q = baseQuery
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &out)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = out as? Data else { throw KeychainError.status(status) }
        return String(decoding: data, as: UTF8.self)
    }

    static func delete() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError.status(status) }
    }
}
