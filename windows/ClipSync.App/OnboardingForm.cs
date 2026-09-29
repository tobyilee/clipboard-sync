using ClipSync.Core;

namespace ClipSync.App;

/// 온보딩: 서버 URL + 첫 기기(Mac)가 만든 passphrase 입력 → 연결 테스트 → Credential Manager 저장 (spec 6.1, 3.2).
/// passphrase 생성과 vault 설정 편집은 Mac 전용이다.
sealed class OnboardingForm : Form
{
    readonly Settings settings;
    readonly TextBox url = new() { Width = 460 };
    readonly TextBox pass = new() { Width = 460, UseSystemPasswordChar = true };
    readonly Label vault = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 8f) };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    readonly Button save = new() { Text = "연결 테스트 후 저장", AutoSize = true };
    public bool Saved { get; private set; }

    public OnboardingForm(Settings settings)
    {
        this.settings = settings;
        Text = "ClipSync 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var show = new CheckBox { Text = "passphrase 표시", AutoSize = true };
        show.CheckedChanged += (_, _) => pass.UseSystemPasswordChar = !show.Checked;
        url.Text = settings.ServerUrl ?? "";
        pass.TextChanged += (_, _) => UpdateVault();

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.AddRange([
            new Label { Text = "서버 URL (예: https://clipsync.example.workers.dev)", AutoSize = true }, url,
            new Label { Text = "passphrase (Mac에서 만든 것을 그대로 입력)", AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, pass, show,
            vault, status, save,
        ]);
        Controls.Add(layout);
        AcceptButton = save;
        save.Click += async (_, _) => await SaveAsync();
    }

    void UpdateVault()
    {
        // PBKDF2 600k는 0.1~0.2초라 입력마다 돌리지 않고 길이 조건을 만족할 때만 표시한다.
        vault.Text = Protocol.NormalizePassphrase(pass.Text).Length >= 24 ? "" : "passphrase는 공백 정리 후 24자 이상";
    }

    async Task SaveAsync()
    {
        if (ServerUrlInput.Parse(url.Text) is not { } uri) { Show("서버 URL은 https://… 형식이어야 합니다 (로컬 개발은 http://localhost 만 허용).", false); return; }
        if (Protocol.NormalizePassphrase(pass.Text).Length < 24) { Show("passphrase는 공백 정리 후 24자 이상이어야 합니다.", false); return; }
        save.Enabled = false;
        Show("연결 중…", true);
        var passphrase = pass.Text;
        try
        {
            var keys = await Task.Run(() => Protocol.DeriveKeys(passphrase));
            vault.Text = "vault_id: " + keys.VaultId;
            using var client = new ServerClient(uri, keys, settings.DeviceId);
            await client.ListAsync(0);
            CredentialStore.Save(passphrase);
            settings.ServerUrl = uri.GetLeftPart(UriPartial.Authority);
            settings.LastSeq = null;   // 첫 연결: hello.seq로 초기화 (과거 항목 자동 적용 방지)
            settings.LastAppliedSeq = null;
            settings.ConfigVersion = 0;   // 다른 vault일 수 있으므로 config 캐시도 비운다
            settings.VaultConfig = VaultConfig.FailClosed;
            settings.Save();
            Log.Info($"onboarding saved server={uri.Host} vault={keys.VaultId[..8]}");
            Saved = true;
            Close();
        }
        catch (ServerException e) when (e.Status == 401)
        {
            Show("인증 실패(401): 서버의 VAULT_ID가 이 passphrase와 다릅니다. Mac에서 만든 passphrase를 그대로 입력했는지 확인하세요.", false);
        }
        catch (Exception e)
        {
            Show("연결 실패: " + e.Message, false);
            Log.Error("onboarding: " + e.Message);
        }
        finally { save.Enabled = true; }
    }

    void Show(string msg, bool ok)
    {
        status.Text = msg;
        status.ForeColor = ok ? SystemColors.ControlText : Color.DarkOrange;
    }
}
