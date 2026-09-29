import Foundation

/// 파일 로그 `~/Library/Logs/ClipSync/clipsync.log` (Windows와 같은 원칙: **클립보드 내용·passphrase는 기록하지 않는다**).
enum Log {
    private static let queue = DispatchQueue(label: "com.tobylee.clipsync.log")
    static let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/ClipSync")
    static let file = dir.appendingPathComponent("clipsync.log")
    private static let maxBytes = 1 << 20

    static func info(_ msg: String) { write("INFO", msg) }
    static func error(_ msg: String) { write("ERROR", msg) }

    private static func write(_ level: String, _ msg: String) {
        let line = "\(Date().formatted(.iso8601.year().month().day().time(includingFractionalSeconds: true))) \(level) \(msg)\n"
        queue.async {
            let fm = FileManager.default
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            if let size = (try? fm.attributesOfItem(atPath: file.path))?[.size] as? Int, size > maxBytes {
                let old = dir.appendingPathComponent("clipsync.log.1")
                try? fm.removeItem(at: old)
                try? fm.moveItem(at: file, to: old)
            }
            if !fm.fileExists(atPath: file.path) { fm.createFile(atPath: file.path, contents: nil) }
            guard let h = try? FileHandle(forWritingTo: file) else { return }
            h.seekToEndOfFile()
            h.write(Data(line.utf8))
            try? h.close()
        }
    }
}
