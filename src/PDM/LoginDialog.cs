using System.Windows;
using System.Windows.Controls;
using PDM.Core.Engine;
using PDM.Core.Security;

namespace PDM;

/// <summary>Username and password for one site; the password is encrypted with DPAPI before it is kept.</summary>
internal static class LoginDialog
{
    /// <summary>Returns the new/changed login, or null when cancelled. An empty password keeps the old one.</summary>
    public static SiteLogin? Edit(Window? owner, SiteLogin? existing, string? host = null)
    {
        var w = new PdmWindow
        {
            Title = Loc.T(existing == null ? "LoginAdd" : "LoginEdit"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
        };
        var hostBox = new TextBox { Text = existing?.Host ?? host ?? "", Padding = new Thickness(4) };
        var userBox = new TextBox { Text = existing?.User ?? "", Padding = new Thickness(4) };
        var passBox = new PasswordBox { Padding = new Thickness(4) };
        var error = Ui.Text("", 12, brush: Theme.Bad, wrap: true);
        SiteLogin? result = null;

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Ui.Field(Loc.T("LoginHost"), hostBox, 120));
        sp.Children.Add(Ui.Field(Loc.T("LoginUser"), userBox, 120));
        sp.Children.Add(Ui.Field(Loc.T("LoginPassword"), passBox, 120));
        if (existing?.ProtectedPassword != null) sp.Children.Add(Ui.Text(Loc.T("PasswordKept"), 12, brush: Theme.SubFg, wrap: true));
        sp.Children.Add(Ui.Text(Loc.T("LoginHint"), 12, brush: Theme.SubFg, wrap: true));
        sp.Children.Add(error);
        sp.Children.Add(Ui.Buttons(
            Ui.Button(Loc.T("Save"), (_, _) =>
            {
                var h = NetConfig.NormalizeHost(hostBox.Text);
                if (h.Length == 0 || h.Contains('/') || h.Contains(' ')) { error.Text = Loc.T("LoginHost") + ": ?"; hostBox.Focus(); return; }
                if (userBox.Text.Trim().Length == 0) { error.Text = Loc.T("LoginUser") + ": ?"; userBox.Focus(); return; }
                if (passBox.Password.Length == 0 && existing?.ProtectedPassword == null) { error.Text = Loc.T("LoginPassword") + ": ?"; passBox.Focus(); return; }
                result = new SiteLogin
                {
                    Host = h,
                    User = userBox.Text.Trim(),
                    ProtectedPassword = passBox.Password.Length > 0 ? Dpapi.Protect(passBox.Password) : existing!.ProtectedPassword,
                };
                w.DialogResult = true;
            }, isDefault: true),
            Ui.Button(Loc.T("Cancel"), isCancel: true)));
        w.Content = sp;
        w.Loaded += (_, _) => (hostBox.Text.Length == 0 ? hostBox : userBox.Text.Length == 0 ? userBox : (Control)passBox).Focus();
        return w.ShowDialog() == true ? result : null;
    }
}
