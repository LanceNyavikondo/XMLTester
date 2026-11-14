using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XMLTester;

// SSH/headless-friendly counterpart to the Avalonia GUI (MainWindow), reusing the same wire
// protocol (EspFrame/EspMessages/EspCurrencies/EspResponseParser/EspCodes) so both front ends
// stay behaviourally identical. Added because the GUI needs an X11 display and fails outright
// over a plain SSH session (System.Exception: XOpenDisplay failed) - confirmed live on Ubuntu
// 18.04, 2026-10-14. Covers the core flow only: Init, Purchase, Balance, Reversal, Close -
// not full GUI parity (no cashback callback prompt loop, no change disbursement).
internal static class ConsoleApp
{
    private static readonly string CONFIG_AGENT_FILE =
        OperatingSystem.IsWindows()
            ? @"C:\postilion\eSocket.POS\configagent_properties.txt"
            : "/opt/postilion/eSocket.POS/configagent_properties.txt";

    private static readonly string PROPERTIES_FILE =
        OperatingSystem.IsWindows()
            ? @"C:\postilion\eSocket.POS\properties.txt"
            : "/opt/postilion/eSocket.POS/properties.txt";

    private const string HOST             = "127.0.0.1";
    private const int    PORT             = 23001;
    private const int    READ_BUFFER_SIZE = 16384;
    private const int    READ_TIMEOUT_S   = 90;

    private static string? _lastTranStan;
    private static long    _lastTranAmount;
    private static string? _lastTranCurrency;

    public static void Run()
    {
        Console.WriteLine("================================================");
        Console.WriteLine("  eSocket.POS XMLTester - console mode");
        Console.WriteLine("================================================");

        string termId = LocateTerminalId();
        Console.Write(string.IsNullOrEmpty(termId) ? "Terminal ID: " : $"Terminal ID [{termId}]: ");
        string? entered = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(entered)) termId = entered.Trim();

        bool initialised = false;

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  (1) Init");
            Console.WriteLine("  (2) Purchase");
            Console.WriteLine("  (3) Balance Inquiry");
            Console.WriteLine("  (4) Reversal (last transaction)");
            Console.WriteLine("  (5) Close");
            Console.WriteLine("  (0) Exit");
            Console.Write("> ");

            string? choice = Console.ReadLine()?.Trim();
            if (choice is null || choice == "0") break;

            switch (choice)
            {
                case "1":
                {
                    string xml = EspMessages.Init(termId);
                    EspResponse? resp = SendReceive(xml, 0, termId);
                    if (resp is { MessageType: "Admin", ActionCode: "APPROVE" })
                    {
                        initialised = true;
                        Console.WriteLine("Initialised - ready.");
                    }
                    break;
                }

                case "2":
                {
                    if (!initialised)
                        Console.WriteLine("(not INIT'd yet - sending anyway)");

                    decimal amount   = PromptDecimal("Amount (e.g. 10.00): ");
                    string  currency = PromptCurrency();
                    decimal cashback = PromptDecimal("Cashback (blank for none): ", allowBlank: true);

                    long   amountMinor   = (long)Math.Round(amount * 100);
                    long   cashbackMinor = (long)Math.Round(cashback * 100);
                    string stan          = NextStan();
                    string ccyCode       = EspCurrencies.GetCode(currency);

                    string xml = EspMessages.Purchase(termId, stan, amountMinor, ccyCode, cashbackMinor);
                    Console.WriteLine($"PURCHASE STAN={stan} | {currency} {ccyCode} | amount={amount:0.00}" +
                                      (cashbackMinor > 0 ? $" | cashback={cashback:0.00}" : ""));
                    EspResponse? resp = SendReceive(xml, cashbackMinor, termId);
                    if (resp?.MessageType == "Transaction")
                    {
                        _lastTranStan     = stan;
                        _lastTranAmount   = amountMinor;
                        _lastTranCurrency = ccyCode;
                    }
                    break;
                }

                case "3":
                {
                    string currency = PromptCurrency();
                    string stan     = NextStan();
                    string ccyCode  = EspCurrencies.GetCode(currency);
                    string xml      = EspMessages.Balance(termId, stan, ccyCode);
                    Console.WriteLine($"BALANCE ENQUIRY STAN={stan} | {currency} {ccyCode}");
                    SendReceive(xml, 0, termId);
                    break;
                }

                case "4":
                {
                    if (_lastTranStan is null)
                    {
                        Console.WriteLine("No transaction to reverse yet.");
                        break;
                    }
                    string xml = EspMessages.Reversal(termId, _lastTranStan, _lastTranAmount, _lastTranCurrency ?? "840");
                    Console.WriteLine($"REVERSAL | orig STAN={_lastTranStan} | amount={_lastTranAmount}");
                    SendReceive(xml, 0, termId);
                    break;
                }

                case "5":
                {
                    string xml = EspMessages.Close(termId);
                    SendReceive(xml, 0, termId);
                    initialised = false;
                    break;
                }

                default:
                    Console.WriteLine("Unrecognised option.");
                    break;
            }
        }
    }

    // Field 11-style STAN, 6 numeric digits. The previous "ymmss" format
    // dropped day/hour/minute entirely - only ~60 distinct values, repeating
    // every minute all day - and "y" alone can be 1 or 2 digits depending on
    // the year, so the width wasn't even reliably 6. Confirmed live: eSocket.POS
    // was rejecting the repeats as RC26 "Duplicate record". Ticks (100ns
    // resolution) mod 1,000,000 keeps the fixed 6-digit width and makes a
    // same-value repeat de facto impossible, even for back-to-back calls.
    private static string NextStan() => (DateTime.Now.Ticks % 1_000_000).ToString("D6");

    private static decimal PromptDecimal(string prompt, bool allowBlank = false)
    {
        while (true)
        {
            Console.Write(prompt);
            string? raw = Console.ReadLine()?.Trim();
            if (allowBlank && string.IsNullOrEmpty(raw)) return 0m;
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal val) && val >= 0)
                return val;
            Console.WriteLine("Enter a positive decimal value (e.g. 1.00).");
        }
    }

    private static string PromptCurrency()
    {
        while (true)
        {
            Console.Write($"Currency [{string.Join("/", EspCurrencies.SupportedCurrencies)}] (default USD): ");
            string? raw = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(raw)) return "USD";
            if (Array.IndexOf(EspCurrencies.SupportedCurrencies, raw) >= 0) return raw;
            Console.WriteLine("Unrecognised currency.");
        }
    }

    // Mirrors MainWindow.LoadTerminalId()/TryReadConfigId()/ExtractTerminalId() so both front
    // ends auto-detect the same way - duplicated rather than shared because MainWindow's
    // version is wired into its own UI event log, and this scope is console-only.
    private static string LocateTerminalId()
    {
        string? id = TryReadConfigId(CONFIG_AGENT_FILE);
        if (id is not null)
        {
            Console.WriteLine($"Terminal ID '{id}' loaded from configagent_properties.txt.");
            return id;
        }

        id = TryReadConfigId(PROPERTIES_FILE);
        if (id is not null)
        {
            Console.WriteLine($"Terminal ID '{id}' loaded from properties.txt.");
            return id;
        }

        Console.WriteLine("Could not auto-detect Terminal ID - enter it manually below.");
        return "";
    }

    private static string? TryReadConfigId(string filePath)
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
            Console.WriteLine($"Error reading {Path.GetFileName(filePath)}: {ex.Message}");
        }
        return null;
    }

    private static string ExtractTerminalId(string configIdValue)
    {
        int idx = configIdValue.LastIndexOf('_');
        if (idx >= 0 && idx < configIdValue.Length - 1)
            return configIdValue[(idx + 1)..];
        return configIdValue.Length >= 8 ? configIdValue[^8..] : configIdValue;
    }

    private static EspResponse? SendReceive(string xmlMessage, long cashbackMinor, string termId)
    {
        Socket?      socket        = null;
        EspResponse? finalResponse = null;
        try
        {
            Console.WriteLine($"Connecting to {HOST}:{PORT}...");
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = READ_TIMEOUT_S * 1000,
                SendTimeout    = 10 * 1000
            };
            socket.Connect(new IPEndPoint(IPAddress.Parse(HOST), PORT));
            Console.WriteLine("Connected.");

            socket.Send(EspFrame.Encode(xmlMessage));

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
                    Console.WriteLine($"No response within {READ_TIMEOUT_S}s - verify eSocket.POS is running on port {PORT}.");
                    break;
                }

                if (bytesRead == 0)
                {
                    Console.WriteLine("Server closed the connection without a response.");
                    break;
                }

                string raw = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                string xml = EspFrame.StripHeader(raw);

                if (xml.Contains("Esp:Callback"))
                {
                    Console.WriteLine("DATA_REQUIRED callback - responding with cashback amount.");
                    socket.Send(EspFrame.Encode(EspMessages.CallbackResponse(termId, cashbackMinor)));
                    continue;
                }

                EspResponse resp = EspResponseParser.Parse(xml);
                PrintResponse(resp);
                finalResponse = resp;

                bool isFinal = resp.MessageType is "Transaction" or "Inquiry" or "Admin" or "Error" or "ParseError";
                if (isFinal) break;
            }
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Connection error ({ex.SocketErrorCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            socket?.Close();
        }
        return finalResponse;
    }

    private static void PrintResponse(EspResponse resp)
    {
        string mrc(string? code) => code is not null ? $" | MRC {code}: {EspCodes.ReasonMessage(code)}" : "";

        switch (resp.MessageType)
        {
            case "Admin":
                Console.WriteLine($"ADMIN {resp.EventId}: {resp.ActionCode}{mrc(resp.MessageReasonCode)}");
                break;

            case "Transaction":
                Console.WriteLine($"TRANSACTION: {resp.ActionCode ?? "UNKNOWN"} | RC {resp.ResponseCode ?? "-"}: " +
                                  $"{EspCodes.ResponseMessage(resp.ResponseCode)}{mrc(resp.MessageReasonCode)}");
                Console.WriteLine($"  Amount: {EspCodes.FormatCurrencyAmount(resp.TransactionAmount, resp.CurrencyCode)}");
                Console.WriteLine($"  RRN: {resp.RetrievalRefNr ?? "-"}  Card: {EspCodes.MaskCard(resp.CardNumber)}");
                break;

            case "Inquiry":
                Console.WriteLine($"BALANCE INQUIRY: {resp.ActionCode ?? "UNKNOWN"}{mrc(resp.MessageReasonCode)}");
                break;

            case "Event":
                string detail = string.IsNullOrEmpty(resp.Detail) ? "" : $" - {resp.Detail}";
                Console.WriteLine($"EVENT: {(resp.EventId ?? "EVENT").Replace("_", " ")}{detail}");
                break;

            case "Error":
                string desc = resp.Detail is not null ? $" - {resp.Detail}" : "";
                Console.WriteLine($"ERROR: {resp.ActionCode} | RC {resp.ResponseCode}: " +
                                  $"{EspCodes.ResponseMessage(resp.ResponseCode)}{mrc(resp.MessageReasonCode)}{desc}");
                break;

            case "ParseError":
                Console.WriteLine("Failed to parse response from eSocket.POS.");
                break;
        }
    }
}
