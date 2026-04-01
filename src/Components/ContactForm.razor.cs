using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using MimeKit;
using AllCompassionateCare.src.Email;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Microsoft.JSInterop;
using System.Text.Json.Serialization;

namespace AllCompassionateCare.src.Components;

public partial class ContactForm
{
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IServiceProvider ServiceProvider { get; set; } = default!;
    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [SupplyParameterFromForm]
    protected ContactFormInput FormInput { get; set; } = new();
    protected string? StatusMessage { get; set; }
    protected bool IsSuccess { get; set; }
    protected bool IsSubmitting { get; set; }
    protected bool CaptchaError { get; set; }
    protected bool ShowSuccessModal { get; set; }
    private bool _needsRecaptchaRender;

    protected override void OnInitialized()
    {
        FormInput ??= new ContactFormInput();
    }
    protected void CloseSuccessModal()
    {
        ShowSuccessModal = false;
        StatusMessage = null;
        StateHasChanged();
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || _needsRecaptchaRender)
        {
            _needsRecaptchaRender = false;
            await Task.Delay(200);
            try
            {
                var siteKey = Configuration["Recaptcha:SiteKey"]!;
                var rendered = await JS.InvokeAsync<bool>("renderRecaptcha", "recaptcha-container", siteKey);
                if (!rendered)
                    Console.WriteLine("[RECAPTCHA] Widget failed to render — check site key and domain settings.");
            }
            catch (JSException ex)
            {
                Console.WriteLine($"[RECAPTCHA ERROR] {ex.Message}");
            }
        }
    }
    protected async Task Submit()
    {
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
                var output = await htmlRenderer.RenderComponentAsync<ContactEmailTemplate>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        { nameof(ContactEmailTemplate.Name),    FormInput.Name },
                        { nameof(ContactEmailTemplate.Email),   FormInput.Email },
                        { nameof(ContactEmailTemplate.Message), FormInput.Message }
                    }));
                return output.ToHtmlString();
            });

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromAddress));
            message.To.Add(new MailboxAddress("All Compassionate Care", toAddress));
            message.ReplyTo.Add(new MailboxAddress(FormInput.Name, FormInput.Email));
            message.Subject = $"New Contact Form Submission from {FormInput.Name}";
            message.Body = new TextPart("html") { Text = htmlBody };

            using var client = new MailKit.Net.Smtp.SmtpClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(username, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            IsSuccess = true;
            ShowSuccessModal = true;
            FormInput = new ContactFormInput();
            _needsRecaptchaRender = true;
            await JS.InvokeVoidAsync("resetCaptcha");
        }
        catch (Exception ex)
        {
            IsSuccess = false;
            StatusMessage = "Failed to send your message.";
            Console.WriteLine($"[SMTP ERROR] {ex.Message}");
            await JS.InvokeVoidAsync("resetCaptcha");
        }
        finally
        {
            IsSubmitting = false;
            StateHasChanged();
        }
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
            Console.WriteLine($"[CAPTCHA ERROR] {ex.Message}");
            return false;
        }
    }

    public class ContactFormInput
    {
        [Required(ErrorMessage = "Name is required.")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email address is required.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Message is required.")]
        public string Message { get; set; } = string.Empty;
    }

    private record RecaptchaResponse(
    [property: JsonPropertyName("success")] bool Success
);
}
