using System.Text;
using System.Xml;

namespace XMLTester;

// Frames an XML string with the eSocket.POS binary length header.
// Messages < 65535 bytes:  2-byte big-endian length prefix.
// Messages >= 65535 bytes: 0xFF 0xFF + 4-byte big-endian length.
internal static class EspFrame
{
    public static byte[] Encode(string xml)
    {
        byte[] msgBytes = Encoding.UTF8.GetBytes(xml);
        int length = msgBytes.Length;

        if (length < 65535)
        {
            byte[] frame = new byte[length + 2];
            frame[0] = (byte)(length / 256);
            frame[1] = (byte)(length % 256);
            Array.Copy(msgBytes, 0, frame, 2, length);
            return frame;
        }
        else
        {
            byte[] frame = new byte[length + 6];
            frame[0] = 0xFF;
            frame[1] = 0xFF;
            byte[] lenBytes = BitConverter.GetBytes(length);
            Array.Reverse(lenBytes);
            Array.Copy(lenBytes, 0, frame, 2, 4);
            Array.Copy(msgBytes, 0, frame, 6, length);
            return frame;
        }
    }

    public static string StripHeader(string raw)
    {
        int idx = raw.IndexOf("<?xml", StringComparison.Ordinal);
        return idx > 0 ? raw[idx..] : raw;
    }
}

internal static class EspCurrencies
{
    private static readonly Dictionary<string, int> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 840,
        ["ZWG"] = 924,
        ["ZMW"] = 967,
        ["ZAR"] = 710,
        ["MWK"] = 454,
        ["BWP"] = 72,
        ["ZIG"] = 932,
        ["GBP"] = 826,
        ["EUR"] = 978,
    };

    public static readonly string[] SupportedCurrencies =
        ["USD", "ZWG", "ZMW", "ZAR", "MWK", "BWP", "ZIG", "GBP", "EUR"];

    public static string GetCode(string currency) =>
        Codes.TryGetValue(currency, out int code) ? code.ToString("D3") : "840";
}

internal static class EspRrn
{
    public static string Generate() => DateTime.Now.ToString("yyMMddHHmmss");
}

internal static class EspMessages
{
    private const string NS = "xmlns:Esp=\"http://www.mosaicsoftware.com/Postilion/eSocket.POS/\"";

    public static string Init(string terminalId) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<Esp:Interface Version=\"1\" {NS}>" +
        $"<Esp:Admin Action=\"INIT\" TerminalId=\"{terminalId}\">" +
        $"<Esp:Register EventId=\"UNSOLICITED_CARD_READ\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"CARD_INSERTED\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"REMOVE_CARD\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"PROMPT_SWIPE_CARD\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"PROMPT_INSERT_CARD\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"PROMPT_PIN\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"PROMPT_TRANSACTION_PROCESSING\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"PIN_ENTRY\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"PROMPT_PAN\" Type=\"EVENT\"/>" +
        $"<Esp:Register EventId=\"DATA_REQUIRED\" Type=\"CALLBACK\"/>" +
        $"</Esp:Admin></Esp:Interface>";

    public static string Purchase(
        string terminalId, string tranId, long amountMinor,
        string currencyCode, long cashbackMinor = 0, string? rrn = null)
    {
        string cashback = cashbackMinor > 0 ? $" CashbackAmount=\"{cashbackMinor}\"" : "";
        string rrnAttr  = rrn is not null    ? $" RetrievalRefNr=\"{rrn}\""          : "";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               $"<Esp:Interface Version=\"1.0\" {NS}>" +
               $"<Esp:Transaction CurrencyCode=\"{currencyCode}\" TerminalId=\"{terminalId}\"" +
               $" TransactionAmount=\"{amountMinor}\"{cashback}" +
               $"{rrnAttr} TransactionId=\"{tranId}\" Type=\"PURCHASE\"/>" +
               $"</Esp:Interface>";
    }

    public static string Reversal(
        string terminalId, string origTranId, long amountMinor,
        string currencyCode, string? rrn = null)
    {
        string rrnAttr = rrn is not null ? $" RetrievalRefNr=\"{rrn}\"" : "";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               $"<Esp:Interface Version=\"1.0\" {NS}>" +
               $"<Esp:Transaction CurrencyCode=\"{currencyCode}\" TerminalId=\"{terminalId}\"" +
               $" TransactionId=\"{origTranId}\" TransactionAmount=\"{amountMinor}\"" +
               $"{rrnAttr} Type=\"PURCHASE\" Reversal=\"TRUE\"/>" +
               $"</Esp:Interface>";
    }

    public static string Balance(string terminalId, string tranId, string currencyCode) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<Esp:Interface Version=\"1.0\" {NS}>" +
        $"<Esp:Inquiry CurrencyCode=\"{currencyCode}\" TerminalId=\"{terminalId}\"" +
        $" TransactionId=\"{tranId}\" Type=\"BALANCE\"/>" +
        $"</Esp:Interface>";

    public static string Close(string terminalId) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<Esp:Interface Version=\"1.0\" {NS}>" +
        $"<Esp:Admin TerminalId=\"{terminalId}\" Action=\"CLOSE\"/>" +
        $"</Esp:Interface>";

    public static string CallbackResponse(string terminalId, long cashbackMinor) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<Esp:Interface Version=\"1.0\" {NS}>" +
        $"<Esp:Callback TerminalId=\"{terminalId}\" EventId=\"DATA_REQUIRED\" ResponseData=\"{cashbackMinor}\"/>" +
        $"</Esp:Interface>";

    // ExtendedTransactionType 6001 routes the DEPOSIT to the correct upstream path.
    public static string ChangeDisbursement(
        string terminalId, string tranId, long amountMinor,
        string currencyCode, string? rrn = null)
    {
        string rrnAttr = rrn is not null ? $" RetrievalRefNr=\"{rrn}\"" : "";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               $"<Esp:Interface Version=\"1.0\" {NS}>" +
               $"<Esp:Transaction CurrencyCode=\"{currencyCode}\" TerminalId=\"{terminalId}\"" +
               $" TransactionAmount=\"{amountMinor}\" TransactionId=\"{tranId}\"" +
               $"{rrnAttr} Type=\"DEPOSIT\" ExtendedTransactionType=\"6001\"/>" +
               $"</Esp:Interface>";
    }
}

internal sealed record EspResponse(
    string  MessageType,
    string? ActionCode,
    string? ResponseCode,
    string? MessageReasonCode,
    string? EventId,
    string? Detail,
    string? RetrievalRefNr,
    string? TerminalId,
    bool    IsCallback,
    string? CardNumber        = null,
    string? TxDateTime        = null,
    string? PanEntryMode      = null,
    string? TransactionAmount = null,
    string? CurrencyCode      = null);

internal static class EspResponseParser
{
    public static EspResponse Parse(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("Esp", "http://www.mosaicsoftware.com/Postilion/eSocket.POS/");
            doc.LoadXml(xml);

            if (xml.Contains("Esp:Callback"))
                return new EspResponse("Callback", null, null, null, null, null, null, null, true);

            if (xml.Contains("Esp:Transaction"))
            {
                var n = doc.SelectSingleNode("//Esp:Transaction", nsm);
                return new EspResponse("Transaction",
                    Attr(n, "ActionCode"), Attr(n, "ResponseCode"),
                    Attr(n, "MessageReasonCode"), null, null,
                    Attr(n, "RetrievalRefNr"), Attr(n, "TerminalId"), false,
                    CardNumber:        Attr(n, "CardNumber"),
                    TxDateTime:        Attr(n, "DateTime"),
                    PanEntryMode:      Attr(n, "PanEntryMode"),
                    TransactionAmount: Attr(n, "TransactionAmount"),
                    CurrencyCode:      Attr(n, "CurrencyCode"));
            }

            if (xml.Contains("Esp:Inquiry"))
            {
                var n = doc.SelectSingleNode("//Esp:Inquiry", nsm);
                return new EspResponse("Inquiry",
                    Attr(n, "ActionCode"), Attr(n, "ResponseCode"),
                    Attr(n, "MessageReasonCode"), Attr(n, "Type"), null,
                    Attr(n, "RetrievalRefNr"), Attr(n, "TerminalId"), false,
                    CardNumber:        Attr(n, "CardNumber"),
                    TxDateTime:        Attr(n, "DateTime"),
                    PanEntryMode:      Attr(n, "PanEntryMode"),
                    TransactionAmount: Attr(n, "TransactionAmount"),
                    CurrencyCode:      Attr(n, "CurrencyCode"));
            }

            if (xml.Contains("Esp:Admin"))
            {
                var n = doc.SelectSingleNode("//Esp:Admin", nsm);
                return new EspResponse("Admin",
                    Attr(n, "ActionCode"), null,
                    Attr(n, "MessageReasonCode"), Attr(n, "Action"), null,
                    null, Attr(n, "TerminalId"), false);
            }

            if (xml.Contains("Esp:Event"))
            {
                var n = doc.SelectSingleNode("//Esp:Event", nsm);
                return new EspResponse("Event", null, null, null,
                    Attr(n, "EventId"), Attr(n, "EventData"),
                    null, Attr(n, "TerminalId"), false);
            }

            if (xml.Contains("Esp:Error"))
            {
                var n = doc.SelectSingleNode("//Esp:Error", nsm);
                return new EspResponse("Error",
                    Attr(n, "ActionCode"), Attr(n, "ResponseCode"),
                    Attr(n, "MessageReasonCode"), null, Attr(n, "Description"),
                    null, Attr(n, "TerminalId"), false);
            }

            return new EspResponse("Unknown", null, null, null, null, null, null, null, false);
        }
        catch
        {
            return new EspResponse("ParseError", null, null, null, null, null, null, null, false);
        }
    }

    private static string? Attr(XmlNode? node, string name) =>
        node?.Attributes?[name]?.Value;
}

internal static class EspXml
{
    public static string PrettyPrint(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent              = true,
                IndentChars         = "  ",
                NewLineChars        = "\n",
                NewLineHandling     = NewLineHandling.Replace,
                OmitXmlDeclaration  = false
            };
            using (var writer = XmlWriter.Create(new StringWriter(sb), settings))
                doc.WriteTo(writer);
            return sb.ToString();
        }
        catch
        {
            return xml;
        }
    }
}

internal static class EspCodes
{
    public static string ResponseMessage(string? code)
    {
        if (code is null) return "—";
        if (!int.TryParse(code, out int rc)) return code;
        return rc switch
        {
            00 => "Approved or completed successfully",
            01 => "Refer to card issuer",
            02 => "Refer to card issuer, special condition",
            03 => "Invalid merchant",
            04 => "Pick up card",
            05 => "Do not honour",
            06 => "Error",
            07 => "Pick up card, special condition",
            08 => "Honour with identification",
            09 => "Request in progress",
            10 => "Approved, partial",
            11 => "Approved, VIP",
            12 => "Invalid transaction",
            13 => "Invalid amount",
            14 => "Invalid card number",
            15 => "No such issuer",
            16 => "Approved, update Track 3",
            17 => "Customer cancellation",
            18 => "Customer dispute",
            19 => "Re-enter transaction",
            20 => "Invalid response",
            21 => "No action taken",
            22 => "Suspected malfunction",
            23 => "Unacceptable transaction fee",
            24 => "File update not supported",
            25 => "Unable to locate record",
            26 => "Duplicate record",
            27 => "File update edit error",
            28 => "File update, file locked",
            29 => "File update failed",
            30 => "Format error",
            31 => "Bank not supported",
            32 => "Completed partially",
            33 => "Expired card, pick up",
            34 => "Suspected fraud, pick up",
            35 => "Contact acquirer, pick up",
            36 => "Restricted card, pick up",
            37 => "Call acquirer security, pick up",
            38 => "PIN tries exceeded, pick up",
            39 => "No credit account",
            40 => "Function not supported",
            41 => "Lost card",
            42 => "No universal account",
            43 => "Stolen card",
            44 => "No investment account",
            51 => "Not sufficient funds",
            52 => "No cheque account",
            53 => "No savings account",
            54 => "Card expired or not yet effective",
            55 => "Incorrect PIN",
            56 => "No card record",
            57 => "Transaction not permitted to cardholder",
            58 => "Transaction not permitted on terminal",
            59 => "Suspected fraud",
            60 => "Contact acquirer",
            61 => "Exceeds withdrawal limit",
            62 => "Restricted card",
            63 => "Security violation",
            64 => "Original amount incorrect",
            65 => "Exceeds withdrawal frequency",
            66 => "Call acquirer security",
            67 => "Hard capture",
            68 => "Response received too late",
            75 => "PIN tries exceeded",
            77 => "Intervene — bank approval required",
            78 => "Intervene — bank approval required for partial amount",
            90 => "Cut-off in progress",
            91 => "Issuer or switch inoperative",
            92 => "Routing error",
            93 => "Violation of law",
            94 => "Duplicate transaction",
            95 => "Reconcile error",
            96 => "System malfunction",
            98 => "Exceeds cash limit",
            _ => $"Code {code}"
        };
    }

    public static string ReasonMessage(string? mrc)
    {
        if (mrc is null) return "—";
        if (!int.TryParse(mrc, out int code)) return mrc;
        return code switch
        {
            9790 => "Upstream response",
            9791 => "Administrative response",
            9792 => "Advice response",
            9793 => "Suspected format error in advice — may not be resent",
            9620 => "EMV offline approved",
            9621 => "EMV offline declined",
            9622 => "EMV approved after online",
            9623 => "EMV declined after online",
            9600 => "System malfunction",
            9601 => "System malfunction (null action)",
            9602 => "Component error — request pipeline",
            9603 => "Component error — response pipeline",
            9604 => "Database error",
            9630 => "Customer cancellation",
            9631 => "Operator cancellation",
            9635 => "Customer timeout",
            9636 => "Card reader retries exceeded",
            9637 => "No supported EMV applications",
            9638 => "Cardholder verification failure",
            9639 => "ICC blocked",
            9640 => "ICC transaction failed",
            9641 => "Device failure",
            9642 => "Fatal printer error",
            9643 => "Card still in slot",
            9644 => "Card insert retries exceeded",
            9650 => "Issuer disconnected",
            9651 => "Issuer timeout before response",
            9660 => "Signature did not match",
            9670 => "Batch totals not available",
            9680 => "Key change in progress",
            9700 => "Missing transaction amount",
            9701 => "Missing card number",
            9702 => "Missing expiry date",
            9703 => "Missing PIN data",
            9704 => "Missing processing code",
            9705 => "Missing account",
            9706 => "Missing cashback amount",
            9707 => "Missing currency code",
            9708 => "Missing merchandise data",
            9709 => "Missing effective date",
            9710 => "Missing card sequence number",
            9711 => "Missing cheque data",
            9720 => "Original transaction not found",
            9721 => "Duplicate transaction",
            9750 => "Expired card",
            9751 => "No supported accounts",
            9752 => "No supported accounts for manual entry",
            9753 => "Card number failed Luhn check",
            9754 => "Card not yet effective",
            9755 => "No supported accounts for ICC fallback",
            9756 => "Not valid for transaction",
            9757 => "Consecutive usage not allowed",
            9758 => "Declined — CVV or AVS failure",
            9759 => "Card number format invalid",
            9760 => "Purchase amount exceeds maximum allowed",
            9761 => "Cashback amount exceeds maximum allowed",
            9762 => "Transaction amount exceeds maximum allowed",
            9763 => "Card sequence number format invalid",
            9764 => "Inconsistent data on chip",
            9765 => "Inconsistent data on Track 2",
            9766 => "Invalid Track 2 data",
            9770 => "Cashback amount exceeds transaction amount",
            9771 => "Cashback present in non-cashback transaction",
            9772 => "Cashback not permitted to cardholder",
            9773 => "Cashback account type invalid",
            9774 => "Cashback currency code invalid",
            1006 => "Under floor limit",
            1007 => "Under offline limit",
            4000 => "Customer cancellation",
            4021 => "Timeout waiting for response",
            _ => $"MRC {code}"
        };
    }

    public static string PanEntryModeDescription(string? code)
    {
        if (code is null) return "—";
        if (!int.TryParse(code, out int c)) return code;
        return c switch
        {
            0  => "Unknown",
            1  => "Manual via key pad",
            2  => "Magnetic stripe",
            3  => "Barcode",
            4  => "OCR",
            5  => "ICC (chip)",
            7  => "ICC contactless",
            90 => "Magnetic stripe (Track 2)",
            91 => "Magnetic contactless",
            95 => "ICC, Track 2",
            _ => $"Mode {c}"
        };
    }

    public static string MaskCard(string? cardNumber)
    {
        if (cardNumber is null || cardNumber.Length < 10) return cardNumber ?? "—";
        int maskLen = cardNumber.Length - 10;
        return cardNumber[..6] + new string('*', maskLen) + cardNumber[^4..];
    }

    // Converts eSocket.POS DateTime attribute (MMddHHmmss) to DD/MM/YYYY HH:mm:ss.
    public static string FormatTxDateTime(string? raw)
    {
        if (raw is null || raw.Length < 10) return raw ?? "—";
        try
        {
            string mon = raw[..2], day = raw[2..4];
            string hr  = raw[4..6], min = raw[6..8], sec = raw[8..10];
            return $"{day}/{mon}/{DateTime.Now.Year:D4} {hr}:{min}:{sec}";
        }
        catch { return raw; }
    }

    public static string FormatCurrencyAmount(string? amountMinor, string? currency)
    {
        if (amountMinor is null) return "—";
        if (!long.TryParse(amountMinor, out long minor)) return amountMinor;
        return $"{currency ?? "?"} $ {minor / 100.0m:0.00}";
    }
}
