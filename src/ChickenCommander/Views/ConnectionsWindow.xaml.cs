using System.Windows;
using System.Windows.Controls;
using ChickenCommander.Core.Remote;
using ChickenCommander.Services;
using Microsoft.Win32;

namespace ChickenCommander.Views;

/// <summary>Saved FTP / SFTP connections: edit, delete, connect. Edits the list it is given in place.</summary>
public partial class ConnectionsWindow : Window
{
    private readonly List<RemoteConnection> connections;
    private bool loading;

    public ConnectionsWindow(List<RemoteConnection> connections)
    {
        InitializeComponent();
        this.connections = connections;
        RefreshList();
        if (connections.Count > 0) List.SelectedIndex = 0;
        else New_Click(this, new RoutedEventArgs());
        Loaded += (_, _) => (connections.Count > 0 ? (Control)List : NameBox).Focus();
    }

    public RemoteConnection? Chosen { get; private set; }

    /// <summary>The password typed in the form (null if left empty: the saved one is used).</summary>
    public string? TypedPassword { get; private set; }

    private void RefreshList(RemoteConnection? select = null)
    {
        List.ItemsSource = null;
        List.ItemsSource = connections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (select is not null) List.SelectedItem = select;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not RemoteConnection c) return;
        loading = true;
        NameBox.Text = c.Name;
        ProtocolBox.SelectedIndex = c.Protocol switch { RemoteProtocol.Sftp => 0, RemoteProtocol.Ftp => 1, _ => 2 };
        HostBox.Text = c.Host;
        PortBox.Text = c.EffectivePort.ToString();
        UserBox.Text = c.User;
        PasswordBox.Password = string.Empty;
        SavePasswordBox.IsChecked = c.EncryptedPassword is not null;
        KeyBox.Text = c.PrivateKeyFile ?? string.Empty;
        FolderBox.Text = c.InitialDirectory;
        FingerprintText.Text = c.HostKeyFingerprint is { } fp ? $"Trusted host key: {fp}"
            : c.AcceptedCertificate is { } cert ? $"Accepted certificate: {cert}"
            : c.EncryptedPassword is not null ? "Password saved (leave the field empty to keep it)." : string.Empty;
        loading = false;
    }

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || PortBox is null) return;
        if (PortBox.Text is "" or "21" or "22") PortBox.Text = ProtocolBox.SelectedIndex == 0 ? "22" : "21";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        List.SelectedItem = null;
        loading = true;
        NameBox.Text = "New connection";
        ProtocolBox.SelectedIndex = 0;
        HostBox.Text = UserBox.Text = KeyBox.Text = string.Empty;
        PortBox.Text = "22";
        PasswordBox.Password = string.Empty;
        SavePasswordBox.IsChecked = false;
        FolderBox.Text = "/";
        FingerprintText.Text = string.Empty;
        loading = false;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not RemoteConnection c) return;
        if (MessageBox.Show(this, $"Delete the connection \"{c.Name}\"?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        connections.Remove(c);
        RefreshList();
        if (connections.Count > 0) List.SelectedIndex = 0;
        else New_Click(sender, e);
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Private key file", Filter = "Key files|*.pem;*.key;*.ppk;id_*|All files|*.*" };
        if (dialog.ShowDialog(this) == true) KeyBox.Text = dialog.FileName;
    }

    /// <summary>Validates the form and saves it into the list (adding or replacing by the selected item).</summary>
    private RemoteConnection? SaveForm()
    {
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        if (name.Length == 0 || host.Length == 0 || name.Contains('/') || name.Contains('\\'))
        {
            MessageBox.Show(this, "Enter a name (without / or \\) and a host.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "The port must be a number between 1 and 65535.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var previous = List.SelectedItem as RemoteConnection;
        if (connections.Any(c => c != previous && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Another connection already has this name.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var protocol = ProtocolBox.SelectedIndex switch { 1 => RemoteProtocol.Ftp, 2 => RemoteProtocol.FtpsExplicit, _ => RemoteProtocol.Sftp };
        var sameServer = previous is not null && previous.Host == host && previous.EffectivePort == port && previous.Protocol == protocol;
        string? encrypted = null;
        if (SavePasswordBox.IsChecked == true)
            encrypted = PasswordBox.Password.Length > 0 ? SecretStore.Protect(PasswordBox.Password) : previous?.EncryptedPassword;

        var connection = new RemoteConnection
        {
            Name = name,
            Protocol = protocol,
            Host = host,
            Port = port,
            User = UserBox.Text.Trim(),
            InitialDirectory = string.IsNullOrWhiteSpace(FolderBox.Text) ? "/" : FolderBox.Text.Trim(),
            PrivateKeyFile = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim(),
            EncryptedPassword = encrypted,
            // A trusted key / certificate only stays valid for the same server.
            HostKeyFingerprint = sameServer ? previous!.HostKeyFingerprint : null,
            AcceptedCertificate = sameServer ? previous!.AcceptedCertificate : null,
        };

        if (previous is not null) connections[connections.IndexOf(previous)] = connection;
        else connections.Add(connection);
        RefreshList(connection);
        return connection;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (SaveForm() is not { } connection) return;
        Chosen = connection;
        TypedPassword = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        DialogResult = true;
    }

    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Connect_Click(sender, e);
}
