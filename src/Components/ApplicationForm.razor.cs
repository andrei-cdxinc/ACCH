using AllCompassionateCare.src.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MimeKit;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AllCompassionateCare.src.Components;

public partial class ApplicationForm
{
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IServiceProvider ServiceProvider { get; set; } = default!;
    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ILogger<ApplicationForm> _logger = default!;

    protected EditContext _editContext = default!;

    [SupplyParameterFromForm]
    protected ApplicationFormInput FormInput { get; set; } = new();

    [Parameter]
    public string? SelectedPosition { get; set; }
    protected string? StatusMessage { get; set; }
    protected bool IsSuccess { get; set; }
    protected bool IsSubmitting { get; set; } = false;
    protected bool CaptchaError { get; set; }
    protected bool ShowSuccessModal { get; set; }
    protected bool IsPositionDisabled { get; set; }
    protected string? ResumeValidationMessage { get; set; }

    protected string MinDateOfBirth =>
    DateOnly.FromDateTime(DateTime.Today)
        .AddYears(-60)
        .AddDays(1)
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    protected string MaxDateOfBirth =>
        DateOnly.FromDateTime(DateTime.Today)
            .AddYears(-18)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private bool _needsRecaptchaRender;

    private const long MaxResumeSizeBytes = 15 * 1024 * 1024; // 15MB
    private static readonly HashSet<string> AllowedResumeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc",
        ".docx"
    };

    protected override void OnInitialized()
    {
        _logger = LoggerFactory.CreateLogger<ApplicationForm>();

        FormInput ??= new ApplicationFormInput();
        _editContext = new EditContext(FormInput);

        if (!string.IsNullOrEmpty(SelectedPosition))
        {
            FormInput.PositionDesired = SelectedPosition;
            IsPositionDisabled = true;
        }
    }

    protected override void OnParametersSet()
    {
        // Update position if parameter changes
        if (!string.IsNullOrEmpty(SelectedPosition) && FormInput.PositionDesired != SelectedPosition)
        {
            FormInput.PositionDesired = SelectedPosition;
            IsPositionDisabled = true;
        }
    }

    protected void CloseSuccessModal()
    {
        ShowSuccessModal = false;
        StatusMessage = null;
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Try to auto-fill position from the URL (id or jobTitle) using the client-side jobs data
            try
            {
                if (string.IsNullOrWhiteSpace(FormInput.PositionDesired) && string.IsNullOrWhiteSpace(SelectedPosition))
                {
                    var jobTitle = await JS.InvokeAsync<string?>("getJobTitleFromQuery");
                    if (!string.IsNullOrEmpty(jobTitle))
                    {
                        FormInput.PositionDesired = jobTitle;
                        IsPositionDisabled = true;
                        StateHasChanged();
                    }
                }
            }
            catch (JSException ex)
            {
                _logger.LogWarning(ex, "JS error while getting job title from query.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while auto-filling position.");
            }

            try
            {
                await JS.InvokeVoidAsync("applyPhoneMask", "phoneInput");
                await JS.InvokeVoidAsync("applyZipCodeMask", "zipCodeInput");
                await JS.InvokeVoidAsync("initCareersResumeUpload", "resumeUpload", MaxResumeSizeBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing JS helpers (phone/zip/resume).");
            }


            if (firstRender || _needsRecaptchaRender)
            {
                _needsRecaptchaRender = false;
                await Task.Delay(200);
                try
                {
                    var siteKey = Configuration["Recaptcha:SiteKey"]!;
                    var rendered = await JS.InvokeAsync<bool>("renderRecaptcha", "recaptcha-container", siteKey);
                    if (!rendered)
                    {
                        _logger.LogWarning("reCAPTCHA failed to render.");
                    }
                }
                catch (JSException ex)
                {
                    _logger.LogWarning(ex, "Error rendering reCAPTCHA.");
                }
            }
        }
    }

    /// Formats a phone number for display
    protected string FormatPhoneNumber(string phoneNumber)
    {
        return PhoneHelper.FormatPhoneNumber(phoneNumber);
    }

    /// Validates a phone number and returns validation feedback
    protected PhoneValidationResult ValidatePhoneNumber(string phoneNumber)
    {
        return PhoneHelper.ValidatePhoneNumber(phoneNumber);
    }

    /// Formats a ZIP code for display (12345 or 12345-6789)
    protected string FormatZipCode(string zipCode)
    {
        return ZipCodeHelper.FormatZipCode(zipCode);
    }

    /// Validates ZIP code and returns validation feedback
    protected ZipValidationResult ValidateZipCode(string zipCode)
    {
        return ZipCodeHelper.ValidateZipCode(zipCode);
    }

    private async Task<bool> ValidateCaptchaAsync(string token)
    {
        try
        {
            using var client = HttpClientFactory.CreateClient();
            var secretKey = Configuration["Recaptcha:SecretKey"]!;

            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("secret", secretKey),
                new KeyValuePair<string, string>("response", token)
            ]);

            var response = await client.PostAsync(
                "https://www.google.com/recaptcha/api/siteverify", content);

            var result = await response.Content.ReadFromJsonAsync<RecaptchaResponse>();
            return result?.Success ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating CAPTCHA.");
            return false;
        }
    }

    private record RecaptchaResponse(
    [property: JsonPropertyName("success")] bool Success
    );

    private sealed class ResumeUploadPayload
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string Base64 { get; set; } = string.Empty;
    }

    protected async Task Submit()
    {
        ResumeValidationMessage = null;

        // Server-side validation guard (in addition to Blazor EditForm)
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(FormInput);
        if (!Validator.TryValidateObject(FormInput, validationContext, validationResults, true))
        {
            IsSuccess = false;
            //StatusMessage = "Please fix the highlighted validation errors.";
            StateHasChanged();
            return;
        }

        ResumeUploadPayload? resumePayload;
        try
        {
            resumePayload = await JS.InvokeAsync<ResumeUploadPayload?>("getCareersResumeUpload");
        }
        catch (JSException ex)
        {
            _logger.LogError(ex, "Resume upload script failed.");
            IsSuccess = false;
            StatusMessage = "Resume upload script is unavailable. Please refresh the page and try again.";
            return;
        }

        if (resumePayload is null || string.IsNullOrWhiteSpace(resumePayload.FileName) || string.IsNullOrWhiteSpace(resumePayload.Base64))
        {
            IsSuccess = false;
            ResumeValidationMessage = "Please upload your resume.";
            StatusMessage = null;
            StateHasChanged();
            return;
        }

        FormInput.ResumeFileName = resumePayload.FileName;
        FormInput.ResumeContentType = resumePayload.ContentType;
        FormInput.ResumeBase64 = resumePayload.Base64;

        // Validate CAPTCHA first
        var captchaToken = await JS.InvokeAsync<string>("getCaptchaToken");
        if (string.IsNullOrEmpty(captchaToken))
        {
            CaptchaError = true;
            StateHasChanged();
            return;
        }

        var captchaValid = await ValidateCaptchaAsync(captchaToken);
        if (!captchaValid)
        {
            CaptchaError = true;
            await JS.InvokeVoidAsync("resetCaptcha");
            StateHasChanged();
            return;
        }

        CaptchaError = false;
        IsSubmitting = true;
        StatusMessage = null;
        StateHasChanged();

        try
        {
            var host = Configuration["Smtp:Host"]!;
            var port = int.Parse(Configuration["Smtp:Port"]!);
            var username = Configuration["Smtp:Username"]!;
            var password = Configuration["Smtp:Password"]!;
            var fromAddress = Configuration["Smtp:FromAddress"]!;
            var fromName = Configuration["Smtp:FromName"]!;
            var toAddress = Configuration["Smtp:ToAddress"]!;

            using var htmlRenderer = new HtmlRenderer(ServiceProvider, LoggerFactory);
            var htmlBody = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await htmlRenderer.RenderComponentAsync<CareerEmailTemplate>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        { nameof(CareerEmailTemplate.FirstName), FormInput.FirstName },
                        { nameof(CareerEmailTemplate.LastName), FormInput.LastName },
                        { nameof(CareerEmailTemplate.DateOfBirth), FormInput.DateOfBirth },
                        { nameof(CareerEmailTemplate.Email), FormInput.Email },
                        { nameof(CareerEmailTemplate.Phone), FormInput.PhoneNumber },
                        { nameof(CareerEmailTemplate.Address), FormInput.Address },
                        { nameof(CareerEmailTemplate.City), FormInput.City },
                        { nameof(CareerEmailTemplate.State), FormInput.State },
                        { nameof(CareerEmailTemplate.ZipCode), FormInput.ZipCode },
                        { nameof(CareerEmailTemplate.PositionDesired), FormInput.PositionDesired },
                        { nameof(CareerEmailTemplate.Comments), FormInput.Comments },
                    }));
                return output.ToHtmlString();
            });

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromAddress));
            message.To.Add(new MailboxAddress("All Compassionate Care", toAddress));
            message.ReplyTo.Add(new MailboxAddress(FormInput.FirstName, FormInput.Email));
            message.Subject = $"New Job Application from {FormInput.FirstName}";

            var resumeExtension = Path.GetExtension(FormInput.ResumeFileName);
            if (string.IsNullOrWhiteSpace(resumeExtension) || !AllowedResumeExtensions.Contains(resumeExtension))
            {
                IsSuccess = false;
                StatusMessage = "Resume format is not supported. Please upload a PDF, DOC, or DOCX file.";
                await JS.InvokeVoidAsync("resetCaptcha");
                return;
            }

            byte[] resumeBytes;
            try
            {
                resumeBytes = Convert.FromBase64String(FormInput.ResumeBase64);
            }
            catch (FormatException)
            {
                IsSuccess = false;
                StatusMessage = "Could not process the uploaded resume file. Please re-select it and try again.";
                await JS.InvokeVoidAsync("resetCaptcha");
                return;
            }

            if (resumeBytes.Length == 0 || resumeBytes.Length > MaxResumeSizeBytes)
            {
                IsSuccess = false;
                StatusMessage = "Could not read the resume file. Please re-select it and try again.";
                await JS.InvokeVoidAsync("resetCaptcha");
                return;
            }

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody
            };

            var contentType = string.IsNullOrWhiteSpace(FormInput.ResumeContentType)
                ? MimeTypes.GetMimeType(FormInput.ResumeFileName)
                : FormInput.ResumeContentType;
            bodyBuilder.Attachments.Add(FormInput.ResumeFileName, resumeBytes, ContentType.Parse(contentType));
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new MailKit.Net.Smtp.SmtpClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(username, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            IsSuccess = true;
            ShowSuccessModal = true;
            ResumeValidationMessage = null;
            FormInput = new ApplicationFormInput();
            // Recreate EditContext for the new model instance so EditForm has a valid context
            _editContext = new EditContext(FormInput);
            _needsRecaptchaRender = true;
            await JS.InvokeVoidAsync("resetCaptcha");
        }
        catch (Exception)
        {
            IsSuccess = false;
            StatusMessage = "Failed to send your application. Please try again or email us.";
            await JS.InvokeVoidAsync("resetCaptcha");
        }
        finally
        {
            IsSubmitting = false;
            StateHasChanged();
        }
    }

    protected void HandleInvalidSubmit(EditContext editContext)
    {
        IsSuccess = false;
        StatusMessage = "Please complete all required fields highlighted below.";
        StateHasChanged();
    }

/// Helper class for phone number formatting and validation
internal static class PhoneHelper
    {
        private static string ExtractDigits(string input)
        {
            return Regex.Replace(input, @"\D", "");
        }
        internal static string FormatPhoneNumber(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var digits = ExtractDigits(input);
            if (digits.Length > 10)
                digits = digits.Substring(0, 10);

            // Format as (XXX) XXX-XXXX
            return digits.Length switch
            {
                0 => string.Empty,
                <= 3 => $"({digits}",
                <= 6 => $"({digits.Substring(0, 3)}) {digits.Substring(3)}",
                _ => $"({digits.Substring(0, 3)}) {digits.Substring(3, 3)}-{digits.Substring(6)}"
            };
        }

        internal static PhoneValidationResult ValidatePhoneNumber(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new PhoneValidationResult { IsValid = true, Message = string.Empty };

            var digits = ExtractDigits(input);

            if (digits[0] == '0' || digits[0] == '1')
                return new PhoneValidationResult
                {
                    IsValid = false,
                    Message = "Invalid area code. Cannot start with 0 or 1"

                };

            if (digits.Length == 10)
                return new PhoneValidationResult
                {
                    IsValid = true,
                    Message = "Valid phone number"
                };

            return new PhoneValidationResult
            {
                IsValid = false,
                Message = "Too many digits"
            };
        }
    }

    /// Helper class for ZIP code formatting and validation
    internal static class ZipCodeHelper
    {
        /// Extracts only digits from the input string
        private static string ExtractDigits(string input)
        {
            return Regex.Replace(input, @"\D", "");
        }

        /// Formats a ZIP code to 12345 or 12345-6789
        internal static string FormatZipCode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var digits = ExtractDigits(input);

            // Truncate to ZIP+4 max length
            if (digits.Length > 9)
                digits = digits[..9];

            return digits.Length switch
            {
                <= 5 => digits,
                _ => $"{digits[..5]}-{digits[5..]}"
            };
        }

        internal static ZipValidationResult ValidateZipCode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new ZipValidationResult { IsValid = true, Message = string.Empty };

            var digits = ExtractDigits(input);

            if (digits.Length == 5 || digits.Length == 9)
            {
                return new ZipValidationResult
                {
                    IsValid = true,
                    Message = "Valid ZIP code"
                };
            }

            if (digits.Length < 5)
            {
                return new ZipValidationResult
                {
                    IsValid = false,
                    Message = $"ZIP code incomplete ({digits.Length}/5 digits)"
                };
            }

            return new ZipValidationResult
            {
                IsValid = false,
                Message = "Invalid ZIP code length"
            };
        }
    }

    public class PhoneValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ZipValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ValidDateOfBirthAttribute : ValidationAttribute
    {
        private const int MinimumAge = 18;
        private const int MaximumAgeExclusive = 60;

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            if (value is null)
                return ValidationResult.Success;

            if (value is not DateOnly dateOfBirth)
                return new ValidationResult("Invalid date of birth.");

            var today = DateOnly.FromDateTime(DateTime.Today);

            if (dateOfBirth >= today)
                return new ValidationResult("Date of birth cannot be today or in the future.");

            var age = today.Year - dateOfBirth.Year;
            if (dateOfBirth.AddYears(age) > today)
                age--;

            if (age < MinimumAge)
                return new ValidationResult($"You must be at least {MinimumAge} years old.");

            if (age >= MaximumAgeExclusive)
                return new ValidationResult($"You must be under {MaximumAgeExclusive} years old.");

            return ValidationResult.Success;
        }
    }

    public class ApplicationFormInput
    {
        [Required(ErrorMessage = "First Name is required.")]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Last Name is required.")]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Date of Birth is required.")]
        public DateOnly? DateOfBirth { get; set; }

        [Required(ErrorMessage = "Email address is required.")]
        [EmailAddress(ErrorMessage = "Invalid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Phone Number is required.")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Address is required.")]
        public string Address { get; set; } = string.Empty;

        [Required(ErrorMessage = "City is required.")]
        public string City { get; set; } = string.Empty;

        [Required(ErrorMessage = "State is required.")]
        public string State { get; set; } = string.Empty;

        [Required(ErrorMessage = "Zip Code is required.")]
        public string ZipCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Position Desired is required.")]
        public string PositionDesired { get; set; } = string.Empty;

        // Resume Upload
        public string ResumeFileName { get; set; } = string.Empty;
        public string ResumeContentType { get; set; } = string.Empty;
        public string ResumeBase64 { get; set; } = string.Empty;

        [StringLength(1000)]
        public string Comments { get; set; } = string.Empty;

        [Required(ErrorMessage ="You must consent to continue")]
        [Range(typeof(bool), "true", "true", ErrorMessage = "You must consent to continue")]
        public bool Consent { get; set; }
    }
}