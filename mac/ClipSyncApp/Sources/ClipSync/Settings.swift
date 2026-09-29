import ClipSyncCore
import Foundation

/// 기기별 설정 (spec 6.1). passphrase는 Keychain, 나머지는 UserDefaults.
@MainActor
final class Settings {
    static let shared = Settings()
    private let d = UserDefaults.standard

    var serverURL: URL? {
        get { d.string(forKey: "serverURL").flatMap(URL.init(string:)) }
        set { d.set(newValue?.absoluteString, forKey: "serverURL") }
    }

    /// 기기 최초 실행 시 생성되는 UUID v4 (소문자 hex 32자, D-33/D-35).
    var deviceId: String {
        if let v = d.string(forKey: "deviceId"), hexDecode(v)?.count == 16 { return v }
        var b = (try? randomBytes(16)) ?? withUnsafeBytes(of: UUID().uuid) { Array($0) }
        b[6] = (b[6] & 0x0F) | 0x40
        b[8] = (b[8] & 0x3F) | 0x80
        let v = uuidHex(b)
        d.set(v, forKey: "deviceId")
        return v
    }

    var syncEnabled: Bool {
        get { d.object(forKey: "syncEnabled") as? Bool ?? true }
        set { d.set(newValue, forKey: "syncEnabled") }
    }
    var sendEnabled: Bool {
        get { d.object(forKey: "sendEnabled") as? Bool ?? true }
        set { d.set(newValue, forKey: "sendEnabled") }
    }
    var receiveEnabled: Bool {
        get { d.object(forKey: "receiveEnabled") as? Bool ?? true }
        set { d.set(newValue, forKey: "receiveEnabled") }
    }
    var pausedUntil: Date? {
        get { d.object(forKey: "pausedUntil") as? Date }
        set { d.set(newValue, forKey: "pausedUntil") }
    }
    /// 목록 워터마크(spec 6.1 `last_seq`): 처리(폐기·건너뜀 포함)한 가장 높은 seq. nil이면 첫 실행(hello.seq로 초기화, D-42).
    var lastSeq: Int? {
        get { d.object(forKey: "lastSeq") as? Int }
        set { d.set(newValue, forKey: "lastSeq") }
    }
    /// 실제로 클립보드에 적용한 가장 높은 seq (spec 6.1 `last_applied_seq`). 일시정지/off 해제 후 catch-up과 로컬 우선 규칙(M6)이 쓴다.
    var lastAppliedSeq: Int? {
        get { d.object(forKey: "lastAppliedSeq") as? Int }
        set { d.set(newValue, forKey: "lastAppliedSeq") }
    }

    /// 지금까지 수용한 가장 높은 config version과 그 설정 (D-50). 없으면 텍스트만 (fail-closed).
    var configVersion: Int {
        get { d.integer(forKey: "configVersion") }
        set { d.set(newValue, forKey: "configVersion") }
    }
    var vaultConfig: VaultConfig {
        get { d.data(forKey: "vaultConfig").flatMap { try? JSONDecoder().decode(VaultConfig.self, from: $0) } ?? .failClosed }
        set { d.set(try? JSONEncoder().encode(newValue), forKey: "vaultConfig") }
    }

    func resetVaultState() {
        for k in ["serverURL", "lastSeq", "lastAppliedSeq", "pausedUntil", "configVersion", "vaultConfig"] { d.removeObject(forKey: k) }
    }
}
