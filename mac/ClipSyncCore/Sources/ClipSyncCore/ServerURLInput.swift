import Foundation

/// 온보딩 입력 검증 (UI와 분리해 테스트 가능하게 둔다).
public enum ServerURLInput {
    /// https만 허용하고, 로컬 개발용으로 localhost/127.0.0.1의 http만 예외로 둔다. 끝의 `/`는 제거한다.
    public static func parse(_ raw: String) -> URL? {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        while s.hasSuffix("/") { s.removeLast() }
        guard let u = URL(string: s), let host = u.host, !host.isEmpty, u.path.isEmpty, u.query == nil else { return nil }
        if u.scheme == "https" { return u }
        if u.scheme == "http", host == "localhost" || host == "127.0.0.1" { return u }
        return nil
    }
}
