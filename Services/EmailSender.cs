using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatApp.Services
{
    public interface IAppEmailSender
    {
        Task<bool> SendAsync(string toAddress, string subject, string htmlBody, string textBody);
    }

    /// <summary>
    /// Sends mail through whichever of three routes is configured, in order:
    ///
    ///   1. an HTTP API (Brevo) - the one that works on hosts like Render,
    ///      which block outbound SMTP ports
    ///   2. SMTP - for a local run against Gmail with an app password
    ///   3. the log - so the whole flow is usable on a machine with neither
    ///
    /// Nothing here holds a credential: they come from configuration, which in
    /// production means environment variables.
    /// </summary>
    public class EmailSender : IAppEmailSender
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;
        private readonly ILogger<EmailSender> _logger;

        public EmailSender(IConfiguration config, IHttpClientFactory http, ILogger<EmailSender> logger)
        {
            _config = config;
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Configuration first, environment variable second - and an empty
        /// string counts as "not set". appsettings.json ships with these keys
        /// present but blank, and treating "" as a value would mean the
        /// environment variable on the server was never even looked at.
        /// </summary>
        private string? Setting(string key, string environmentVariable)
        {
            var value = _config[key];
            if (string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(environmentVariable);

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private string FromAddress =>
            Setting("Email:From", "EMAIL_FROM") ?? "no-reply@chatapp.local";

        private string FromName =>
            Setting("Email:FromName", "EMAIL_FROM_NAME") ?? "ChatApp";

        /// <summary>
        /// Which route mail will take, for the startup log. Without this the
        /// only way to find out that nothing is configured is to request a
        /// code and go looking for it.
        /// </summary>
        public static string DescribeProvider(IConfiguration config)
        {
            string? Value(string key, string env)
            {
                var v = config[key];
                if (string.IsNullOrWhiteSpace(v)) v = Environment.GetEnvironmentVariable(env);
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }

            if (Value("Email:BrevoApiKey", "BREVO_API_KEY") != null)
                return "Brevo API";

            var host = Value("Email:Smtp:Host", "SMTP_HOST");
            if (host != null) return $"SMTP ({host})";

            return "none - reset codes will be written to this log instead of emailed";
        }

        public async Task<bool> SendAsync(string toAddress, string subject, string htmlBody, string textBody)
        {
            var brevoKey = Setting("Email:BrevoApiKey", "BREVO_API_KEY");
            if (!string.IsNullOrWhiteSpace(brevoKey))
                return await SendViaBrevoAsync(brevoKey, toAddress, subject, htmlBody, textBody);

            var smtpHost = Setting("Email:Smtp:Host", "SMTP_HOST");
            if (!string.IsNullOrWhiteSpace(smtpHost))
                return await SendViaSmtpAsync(smtpHost, toAddress, subject, htmlBody);

            // Nothing configured. The flow still works end to end - the code is
            // in the server log - which is what makes this runnable on a fresh
            // clone with no accounts anywhere.
            _logger.LogWarning(
                "No email provider configured. Mail to {To} was NOT sent. Subject: {Subject}\n{Body}",
                toAddress, subject, textBody);

            return false;
        }

        private async Task<bool> SendViaBrevoAsync(string apiKey, string to, string subject,
                                                   string htmlBody, string textBody)
        {
            try
            {
                var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
                request.Headers.Add("api-key", apiKey);
                request.Content = JsonContent.Create(new
                {
                    sender = new { email = FromAddress, name = FromName },
                    to = new[] { new { email = to } },
                    subject,
                    htmlContent = htmlBody,
                    textContent = textBody
                });

                var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode) return true;

                // The body carries the actual reason - an unverified sender,
                // a bad key - and it is worth having in the log.
                var detail = await response.Content.ReadAsStringAsync();
                _logger.LogError("Brevo refused the message ({Status}): {Detail}",
                    (int)response.StatusCode, detail);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending mail through Brevo failed");
                return false;
            }
        }

        private async Task<bool> SendViaSmtpAsync(string host, string to, string subject, string htmlBody)
        {
            try
            {
                var port = int.TryParse(Setting("Email:Smtp:Port", "SMTP_PORT"), out var p) ? p : 587;
                var user = Setting("Email:Smtp:User", "SMTP_USER");
                var password = Setting("Email:Smtp:Password", "SMTP_PASSWORD");

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl = true,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };

                if (!string.IsNullOrWhiteSpace(user))
                    client.Credentials = new NetworkCredential(user, password);

                using var message = new MailMessage
                {
                    From = new MailAddress(FromAddress, FromName),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true
                };
                message.To.Add(to);

                await client.SendMailAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending mail over SMTP failed");
                return false;
            }
        }
    }
}
