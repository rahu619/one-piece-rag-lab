using System.Text.RegularExpressions;

namespace OnePieceApi.Safety;

/// <summary>
/// Detects and masks common shapes of personal data (emails, phone numbers, national IDs,
/// payment card numbers). The patterns are intentionally conservative: false negatives are
/// acceptable for a fan-made episode assistant, false positives on episode data are not, so
/// the patterns target shapes that never occur in the One Piece dataset.
/// </summary>
public static partial class PiiRedactor
{
    public const string EmailPlaceholder = "[REDACTED_EMAIL]";
    public const string PhonePlaceholder = "[REDACTED_PHONE]";
    public const string NationalIdPlaceholder = "[REDACTED_ID]";
    public const string CardNumberPlaceholder = "[REDACTED_CARD]";

    [GeneratedRegex(@"[\w\.\-\+]+@[\w\-]+(\.[\w\-]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    // NANP-style numbers: (555) 123-4567, +1 555 123 4567, 555.123.4567. Ten digits minimum,
    // so episode numbers and years in the dataset can never match.
    [GeneratedRegex(@"\b(?:\+\d{1,3}[\s\-.]?)?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex NationalIdRegex();

    // 13-19 digits, optionally grouped. Longer than any number in the episode dataset.
    [GeneratedRegex(@"\b(?:\d[ \-]?){13,19}\b")]
    private static partial Regex CardNumberRegex();

    public static bool ContainsPii(string text) =>
        EmailRegex().IsMatch(text)
        || PhoneRegex().IsMatch(text)
        || NationalIdRegex().IsMatch(text)
        || CardNumberRegex().IsMatch(text);

    /// <summary>
    /// Replaces every detected PII shape with a typed placeholder so the text can be logged
    /// or displayed without leaking personal data.
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = EmailRegex().Replace(text, EmailPlaceholder);
        redacted = NationalIdRegex().Replace(redacted, NationalIdPlaceholder);
        redacted = CardNumberRegex().Replace(redacted, CardNumberPlaceholder);

        // Phones run last: the card pattern consumes long digit runs first, and doing this
        // before cards would turn a 16-digit card number into a mangled phone placeholder.
        redacted = PhoneRegex().Replace(redacted, PhonePlaceholder);

        return redacted;
    }
}
