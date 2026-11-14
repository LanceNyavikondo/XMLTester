using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace XMLTester;

public partial class MainWindow : Window
{
    private static readonly string CONFIG_AGENT_FILE =
        OperatingSystem.IsWindows()
            ? @"C:\postilion\eSocket.POS\configagent_properties.txt"
            : "/opt/postilion/eSocket.POS/configagent_properties.txt";

    private static readonly string PROPERTIES_FILE =
        OperatingSystem.IsWindows()
            ? @"C:\postilion\eSocket.POS\properties.txt"
            : "/opt/postilion/eSocket.POS/properties.txt";

    private const string HOST              = "127.0.0.1";
    private const int    PORT              = 23001;
    private const int    READ_BUFFER_SIZE  = 16384;
    private const int    CONNECT_TIMEOUT_S = 10;
    private const int    READ_TIMEOUT_S    = 90;

    private bool _initialised;

    private string? _lastTranStan;
    private long    _lastTranAmount;
    private string? _lastTranCurrency;
    private string? _lastTranRrn;

    private readonly ObservableCollection<EventLine> _events = new();

    // Field 11-style STAN, 6 numeric digits. The previous "ymmss" format
    // dropped day/hour/minute entirely - only ~60 distinct values, repeating
    // every minute all day - and "y" alone can be 1 or 2 digits depending on
    // the year, so the width wasn't even reliably 6. Confirmed live: eSocket.POS
    // was rejecting the repeats as RC26 "Duplicate record". Ticks (100ns
    // resolution) mod 1,000,000 keeps the fixed 6-digit width and makes a
    // same-value repeat de facto impossible, even for back-to-back calls.
    private static string NextStan() => (DateTime.Now.Ticks % 1_000_000).ToString("D6");

    public MainWindow()
    {
        InitializeComponent();
        lstEvents.ItemsSource = _events;
        cmbCurrency.ItemsSource   = EspCurrencies.SupportedCurrencies;
        cmbCurrency.SelectedIndex = 0;
        LoadTerminalId();
    }

    // ── Dispatcher helper ─────────────────────────────────────────────────

    private void OnUI(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // ── Terminal ID loading ───────────────────────────────────────────────

    private static string ExtractTerminalId(string configIdValue)
    {
        int idx = configIdValue.LastIndexOf('_');
        if (idx >= 0 && idx < configIdValue.Length - 1)
            return configIdValue[(idx + 1)..];
        return configIdValue.Length >= 8 ? configIdValue[^8..] : configIdValue;
    }

    private string? TryReadConfigId(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                if (!line.Contains("postilion.esocketpos.ConfigId")) continue;
                int eqIdx = line.IndexOf('=');
                if (eqIdx < 0 || eqIdx >= line.Length - 1) continue;
                string value = line[(eqIdx + 1)..].Trim();
                if (value.Length > 0) return ExtractTerminalId(value);
            }
        }
        catch (Exception ex)
        {
            AppendEvent($"Error reading {Path.GetFileName(filePath)}: {ex.Message}", Brushes.Red);
        }
        return null;
    }

    private void LoadTerminalId()
    {
        string? termId = TryReadConfigId(CONFIG_AGENT_FILE);
        if (termId is not null)
        {
            txtTerminalId.Text = termId;
            AppendEvent($"Terminal ID '{termId}' loaded from configagent_properties.txt.", Brushes.DarkGreen);
            return;
        }

        AppendEvent(
            !File.Exists(CONFIG_AGENT_FILE)
                ? "configagent_properties.txt not found — trying properties.txt."
                : "ConfigId key not found in configagent_properties.txt — trying properties.txt.",
            Brushes.DarkOrange);

        termId = TryReadConfigId(PROPERTIES_FILE);
        if (termId is not null)
        {
            txtTerminalId.Text = termId;
            AppendEvent($"Terminal ID '{termId}' loaded from properties.txt.", Brushes.DarkGreen);
            return;
        }

        AppendEvent(
            !File.Exists(PROPERTIES_FILE)
                ? "properties.txt not found. Enter Terminal ID manually."
                : "ConfigId key not found in properties.txt. Enter Terminal ID manually.",
            Brushes.Red);
    }

    // ── UI helpers ────────────────────────────────────────────────────────

    private void AppendEvent(string text, IBrush? brush = null)
    {
        OnUI(() =>
        {
            _events.Add(new EventLine
            {
                Text       = $"[{DateTime.Now:HH:mm:ss}] {text}",
                Foreground = brush ?? Brushes.Black
            });
            Dispatcher.UIThread.Post(
                () => eventsScroll.ScrollToEnd(),
                DispatcherPriority.Background);
        });
    }

    private void ShowXmlSent(string xml)
    {
        OnUI(() => txtXmlSent.Text = EspXml.PrettyPrint(xml));
    }

    private void AppendXmlReceived(string xml)
    {
        OnUI(() =>
        {
            string pretty = EspXml.PrettyPrint(xml);
            txtXmlReceived.Text = string.IsNullOrEmpty(txtXmlReceived.Text)
                ? pretty + "\n"
                : txtXmlReceived.Text + "\n" + new string('─', 72) + "\n" + pretty + "\n";
        });
    }

    private void SetStatus(string text)
    {
        OnUI(() => lblStatus.Text = text);
    }

    private void UpdateOutcome(EspResponse resp)
    {
        OnUI(() =>
        {
            if (resp.ActionCode is not null)
            {
                lblOutcome.Text     = resp.ActionCode;
                lblOutcome.Foreground = resp.ActionCode switch
                {
                    "APPROVE" => Brushes.DarkGreen,
                    "DECLINE" => Brushes.Firebrick,
                    "RETAIN"  => Brushes.DarkRed,
                    "AUTH"    => Brushes.DarkOrange,
                    _         => Brushes.Navy
                };
            }

            if (resp.ResponseCode is not null)
            {
                lblResponseCode.Text = resp.ResponseCode;
                lblResponseMsg.Text  = EspCodes.ResponseMessage(resp.ResponseCode);
            }

            if (resp.MessageReasonCode is not null)
                lblReasonCode.Text = $"{resp.MessageReasonCode} — {EspCodes.ReasonMessage(resp.MessageReasonCode)}";
        });
    }

    private void UpdateTxDetails(EspResponse resp)
    {
        OnUI(() =>
        {
            lblTxTermId.Text   = resp.TerminalId ?? "—";
            lblTxRrn.Text      = resp.RetrievalRefNr ?? "—";
            lblTxCard.Text     = EspCodes.MaskCard(resp.CardNumber);
            lblTxDateTime.Text = EspCodes.FormatTxDateTime(resp.TxDateTime);
            lblTxMethod.Text   = EspCodes.PanEntryModeDescription(resp.PanEntryMode);

            lblTxResponse.Text = resp.ResponseCode is not null
                ? $"{resp.ResponseCode} — {EspCodes.ResponseMessage(resp.ResponseCode)}"
                : "—";

            string? selectedCcy = cmbCurrency.SelectedItem?.ToString();
            lblTxAmount.Text = EspCodes.FormatCurrencyAmount(resp.TransactionAmount, selectedCcy);
        });
    }

    private void ClearOutcome()
    {
        OnUI(() =>
        {
            lblOutcome.Text      = "—";
            lblOutcome.Foreground = Brushes.Black;
            lblResponseCode.Text  = "—";
            lblResponseMsg.Text   = "—";
            lblReasonCode.Text    = "—";
            lblTxTermId.Text      = "—";
            lblTxRrn.Text         = "—";
            lblTxCard.Text        = "—";
            lblTxDateTime.Text    = "—";
            lblTxMethod.Text      = "—";
            lblTxResponse.Text    = "—";
            lblTxAmount.Text      = "—";
        });
    }

    private void SetBusy(bool busy)
    {
        OnUI(() =>
        {
            btnInit.IsEnabled       = !busy;
            btnPurchase.IsEnabled   = !busy && _initialised;
            btnBalance.IsEnabled    = !busy && _initialised;
            btnClose.IsEnabled      = !busy && _initialised;
            btnChange.IsEnabled     = !busy && _initialised;
            btnReversal.IsEnabled   = !busy && _lastTranStan is not null;
            txtAmount.IsEnabled     = !busy;
            txtCashback.IsEnabled   = !busy;
            txtTerminalId.IsEnabled = !busy;
            cmbCurrency.IsEnabled   = !busy;
            chkRrn.IsEnabled        = !busy;
            txtRrn.IsEnabled        = !busy && chkRrn.IsChecked == true;
            btnRegenRrn.IsEnabled   = !busy && chkRrn.IsChecked == true;
        });
    }

    private string TerminalId => txtTerminalId.Text?.Trim() ?? "";

    private string SelectedCurrencyCode =>
        EspCurrencies.GetCode(cmbCurrency.SelectedItem?.ToString() ?? "USD");

    // ── Button handlers ───────────────────────────────────────────────────

    private async void BtnInit_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TerminalId))
        {
            AppendEvent("Terminal ID is required before initialising.", Brushes.Red);
            return;
        }

        string xml = EspMessages.Init(TerminalId);
        ClearOutcome();
        SetBusy(true);
        SetStatus("Initialising...");
        AppendEvent($"Sending INIT for terminal '{TerminalId}'...", Brushes.Navy);
        ShowXmlSent(xml);
        txtXmlReceived.Text = "";

        try   { await SendReceive(xml); }
        finally { SetBusy(false); }
    }

    private async void BtnPurchase_Click(object? sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(txtAmount.Text?.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
        {
            AppendEvent("Invalid amount — enter a positive decimal value (e.g. 1.00).", Brushes.Red);
            return;
        }

        long amountMinor   = (long)Math.Round(amount * 100);
        long cashbackMinor = 0;

        if (!string.IsNullOrWhiteSpace(txtCashback.Text))
        {
            if (!decimal.TryParse(txtCashback.Text.Trim(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out decimal cb) || cb < 0)
            {
                AppendEvent("Invalid cashback amount.", Brushes.Red);
                return;
            }
            cashbackMinor = (long)Math.Round(cb * 100);
        }

        string  stan    = NextStan();
        string  ccyCode = SelectedCurrencyCode;
        string? rrn     = chkRrn.IsChecked == true && !string.IsNullOrWhiteSpace(txtRrn.Text)
                          ? txtRrn.Text.Trim() : null;

        string xml = EspMessages.Purchase(TerminalId, stan, amountMinor, ccyCode, cashbackMinor, rrn);

        ClearOutcome();
        SetBusy(true);
        SetStatus("Processing...");

        string log = $"PURCHASE STAN={stan} | {cmbCurrency.SelectedItem} {ccyCode} | amount={amount:0.00}" +
                     (cashbackMinor > 0 ? $" | cashback={cashbackMinor}" : "") +
                     (rrn is not null   ? $" | RRN={rrn}" : "");
        AppendEvent(log, Brushes.Navy);
        ShowXmlSent(xml);
        txtXmlReceived.Text = "";

        EspResponse? finalResp = null;
        try
        {
            finalResp = await SendReceive(xml, cashbackMinor);
        }
        finally
        {
            if (finalResp?.MessageType == "Transaction")
            {
                _lastTranStan     = stan;
                _lastTranAmount   = amountMinor;
                _lastTranCurrency = ccyCode;
                _lastTranRrn      = finalResp.RetrievalRefNr;
            }
            SetBusy(false);
        }
    }

    private async void BtnBalance_Click(object? sender, RoutedEventArgs e)
    {
        string stan    = NextStan();
        string ccyCode = SelectedCurrencyCode;
        string xml     = EspMessages.Balance(TerminalId, stan, ccyCode);

        ClearOutcome();
        SetBusy(true);
        SetStatus("Balance enquiry...");
        AppendEvent($"BALANCE ENQUIRY STAN={stan} | {cmbCurrency.SelectedItem} {ccyCode}", Brushes.Navy);
        ShowXmlSent(xml);
        txtXmlReceived.Text = "";

        try   { await SendReceive(xml); }
        finally { SetBusy(false); }
    }

    private async void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        string xml = EspMessages.Close(TerminalId);
        ClearOutcome();
        SetBusy(true);
        SetStatus("Closing...");
        AppendEvent("Sending CLOSE...", Brushes.Navy);
        ShowXmlSent(xml);
        txtXmlReceived.Text = "";

        try   { await SendReceive(xml); }
        finally { SetBusy(false); }
    }

    private async void BtnReversal_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastTranStan is null) return;

        string xml = EspMessages.Reversal(
            TerminalId, _lastTranStan, _lastTranAmount,
            _lastTranCurrency ?? SelectedCurrencyCode, _lastTranRrn);

        ClearOutcome();
        SetBusy(true);
        SetStatus("Sending reversal...");
        AppendEvent(
            $"REVERSAL | orig STAN={_lastTranStan} | amount={_lastTranAmount} | RRN={_lastTranRrn ?? "none"}",
            Brushes.DarkOrange);
        ShowXmlSent(xml);
        txtXmlReceived.Text = "";

        try   { await SendReceive(xml); }
        finally { SetBusy(false); }
    }

    private async void BtnChange_Click(object? sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(txtAmount.Text?.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
        {
            AppendEvent("Enter the change amount in the Amount field.", Brushes.Red);
            return;
        }

        long   amountMinor = (long)Math.Round(amount * 100);
        string stan        = NextStan();
        string? rrn        = chkRrn.IsChecked == true && !string.IsNullOrWhiteSpace(txtRrn.Text)
                             ? txtRrn.Text.Trim() : null;

        string ccyCode = SelectedCurrencyCode;
        string xml = EspMessages.ChangeDisbursement(TerminalId, stan, amountMinor, ccyCode, rrn);

        ClearOutcome();
        SetBusy(true);
        SetStatus("Change disbursement...");
        AppendEvent(
            $"CHANGE DISBURSEMENT STAN={stan} | {cmbCurrency.SelectedItem} {ccyCode} | amount={amount:0.00}" +
            (rrn is not null ? $" | RRN={rrn}" : ""),
            Brushes.DarkSlateBlue);
        ShowXmlSent(xml);
        txtXmlReceived.Text = "";

        try   { await SendReceive(xml); }
        finally { SetBusy(false); }
    }

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        _events.Clear();
        txtXmlSent.Text     = "";
        txtXmlReceived.Text = "";
        ClearOutcome();
        SetStatus(_initialised ? "Initialised — ready." : "Ready.");
    }

    private void ChkRrn_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        bool on = chkRrn.IsChecked == true;
        txtRrn.IsEnabled      = on;
        btnRegenRrn.IsEnabled = on;
        if (on && string.IsNullOrWhiteSpace(txtRrn.Text))
            txtRrn.Text = EspRrn.Generate();
    }

    private void BtnRegenRrn_Click(object? sender, RoutedEventArgs e)
    {
        txtRrn.Text = EspRrn.Generate();
    }

    // ── Amount box formatting ─────────────────────────────────────────────

    private void AmountBox_GotFocus(object? sender, Avalonia.Input.GotFocusEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    private void AmountBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (decimal.TryParse(tb.Text?.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal val))
            tb.Text = val.ToString("0.00", CultureInfo.InvariantCulture);
        else if (!string.IsNullOrWhiteSpace(tb.Text))
            tb.Text = "0.00";
    }

    // ── Core TCP send/receive ─────────────────────────────────────────────

    private async Task<EspResponse?> SendReceive(string xmlMessage, long cashbackMinor = 0)
    {
        string termId = TerminalId;
        AppendEvent($"Connecting to {HOST}:{PORT}...", Brushes.DimGray);

        try
        {
            return await Task.Run(() => SendReceiveSync(xmlMessage, cashbackMinor, termId));
        }
        catch (SocketException ex)
        {
            AppendEvent($"Connection error ({ex.SocketErrorCode}): {ex.Message}", Brushes.Red);
            SetStatus("Connection failed.");
        }
        catch (Exception ex)
        {
            AppendEvent($"Error: {ex.Message}", Brushes.Red);
            SetStatus("Error.");
        }

        return null;
    }

    private EspResponse? SendReceiveSync(string xmlMessage, long cashbackMinor, string termId)
    {
        EspResponse? finalResponse = null;
        Socket?      socket        = null;

        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = READ_TIMEOUT_S    * 1000,
                SendTimeout    = CONNECT_TIMEOUT_S * 1000
            };
            socket.Connect(new IPEndPoint(IPAddress.Parse(HOST), PORT));
            AppendEvent("Connected.", Brushes.DarkGreen);

            byte[] frame = EspFrame.Encode(xmlMessage);
            AppendEvent($"TX {frame.Length} bytes [hdr {frame[0]:X2} {frame[1]:X2}]", Brushes.DimGray);
            socket.Send(frame);
            SetStatus("Waiting for eSocket.POS...");

            while (true)
            {
                byte[] buffer = new byte[READ_BUFFER_SIZE];
                int bytesRead;
                try
                {
                    bytesRead = socket.Receive(buffer);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    AppendEvent(
                        $"No response within {READ_TIMEOUT_S}s — verify eSocket.POS is running on port {PORT}.",
                        Brushes.Red);
                    SetStatus("Timed out.");
                    break;
                }

                if (bytesRead == 0)
                {
                    AppendEvent("Server closed the connection without a response.", Brushes.DarkOrange);
                    break;
                }

                AppendEvent($"RX {bytesRead} bytes [hdr {buffer[0]:X2} {buffer[1]:X2}]", Brushes.DimGray);

                string raw = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                string xml = EspFrame.StripHeader(raw);

                if (xml.Contains("Esp:Callback"))
                {
                    AppendEvent("DATA_REQUIRED callback — responding with cashback amount.", Brushes.DarkMagenta);
                    string cbXml   = EspMessages.CallbackResponse(termId, cashbackMinor);
                    byte[] cbFrame = EspFrame.Encode(cbXml);
                    ShowXmlSent(cbXml);
                    AppendEvent(
                        $"TX {cbFrame.Length} bytes (callback) [hdr {cbFrame[0]:X2} {cbFrame[1]:X2}]",
                        Brushes.DimGray);
                    socket.Send(cbFrame);
                    continue;
                }

                AppendXmlReceived(xml);
                EspResponse resp = EspResponseParser.Parse(xml);
                ProcessResponse(resp);
                finalResponse = resp;

                bool isFinal = resp.MessageType is "Transaction" or "Inquiry" or "Admin" or "Error" or "ParseError";
                if (isFinal) break;
            }
        }
        finally
        {
            socket?.Close();
        }

        return finalResponse;
    }

    private void ProcessResponse(EspResponse resp)
    {
        switch (resp.MessageType)
        {
            case "Event":
                string evtLabel = (resp.EventId ?? "EVENT").Replace("_", " ");
                string evtData  = string.IsNullOrEmpty(resp.Detail) ? "" : $" — {resp.Detail}";
                AppendEvent($"EVENT: {evtLabel}{evtData}", Brushes.DarkCyan);
                break;

            case "Admin":
                string adminAction  = resp.EventId ?? "ADMIN";
                string adminOutcome = resp.ActionCode ?? "";
                string mrcAdmin     = resp.MessageReasonCode is not null
                    ? $" | MRC {resp.MessageReasonCode}: {EspCodes.ReasonMessage(resp.MessageReasonCode)}" : "";
                AppendEvent($"ADMIN {adminAction}: {adminOutcome}{mrcAdmin}",
                    adminOutcome == "APPROVE" ? Brushes.DarkGreen : Brushes.Firebrick);
                UpdateOutcome(resp);

                if (adminAction == "INIT" && adminOutcome == "APPROVE")
                {
                    _initialised = true;
                    SetStatus("Initialised — ready.");
                }
                else if (adminAction == "CLOSE")
                {
                    _initialised = false;
                    SetStatus("Terminal closed.");
                }
                break;

            case "Transaction":
                string txOutcome = resp.ActionCode ?? "UNKNOWN";
                string rc        = resp.ResponseCode ?? "—";
                string mrcTx     = resp.MessageReasonCode is not null
                    ? $" | MRC {resp.MessageReasonCode}: {EspCodes.ReasonMessage(resp.MessageReasonCode)}" : "";
                AppendEvent(
                    $"TRANSACTION: {txOutcome} | RC {rc}: {EspCodes.ResponseMessage(resp.ResponseCode)}{mrcTx}",
                    txOutcome == "APPROVE" ? Brushes.DarkGreen : Brushes.Firebrick);
                UpdateOutcome(resp);
                UpdateTxDetails(resp);
                SetStatus($"Transaction {txOutcome}.");
                break;

            case "Inquiry":
                string iqOutcome = resp.ActionCode ?? "UNKNOWN";
                string mrcIq     = resp.MessageReasonCode is not null
                    ? $" | MRC {resp.MessageReasonCode}: {EspCodes.ReasonMessage(resp.MessageReasonCode)}" : "";
                AppendEvent($"BALANCE INQUIRY: {iqOutcome}{mrcIq}",
                    iqOutcome == "APPROVE" ? Brushes.DarkGreen : Brushes.Firebrick);
                UpdateOutcome(resp);
                UpdateTxDetails(resp);
                SetStatus($"Balance {iqOutcome}.");
                break;

            case "Error":
                string errMrc  = resp.MessageReasonCode is not null
                    ? $" | MRC {resp.MessageReasonCode}: {EspCodes.ReasonMessage(resp.MessageReasonCode)}" : "";
                string errDesc = resp.Detail is not null ? $" — {resp.Detail}" : "";
                AppendEvent(
                    $"ERROR: {resp.ActionCode} | RC {resp.ResponseCode}: {EspCodes.ResponseMessage(resp.ResponseCode)}{errMrc}{errDesc}",
                    Brushes.Red);
                UpdateOutcome(resp);
                SetStatus("Error received.");
                break;

            case "ParseError":
                AppendEvent("Failed to parse response from eSocket.POS.", Brushes.Red);
                SetStatus("Parse error.");
                break;

            default:
                AppendEvent($"Unknown response type '{resp.MessageType}'.", Brushes.Gray);
                break;
        }
    }
}
