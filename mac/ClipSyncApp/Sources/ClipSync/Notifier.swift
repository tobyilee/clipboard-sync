import Foundation
import UserNotifications

/// 사용자 조치가 필요한 알림 (spec 6.6, D-52). 최초 1회 사용자 허용이 필요하다.
@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    static let shared = Notifier()

    func requestAuthorization() {
        let c = UNUserNotificationCenter.current()
        c.delegate = self
        c.requestAuthorization(options: [.alert, .sound]) { granted, error in
            Log.info("notification permission granted=\(granted) \(error.map { "\($0)" } ?? "")")
        }
    }

    func post(_ body: String) {
        let content = UNMutableNotificationContent()
        content.title = "ClipSync"
        content.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    /// 메뉴바 앱이 앞에 있을 때도 배너를 보인다.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async
        -> UNNotificationPresentationOptions { [.banner, .sound] }
}
