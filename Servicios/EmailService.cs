//Servicios/EmailService.cs

using ClosedXML.Excel;
using MimeKit;
using Servicios.DTOs;
using Servicios.Interfaz;
using Servicios.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;

using MimeKit.Utils;

namespace Servicios
{
    public class EmailService : IEmailService
    {
        private readonly EmailSettings _settings;
        public EmailService(EmailSettings settings)
        {
            _settings = settings;
        }

        public async Task<List<string>> SendBulkEmailsAsync(IEnumerable<string> emails, string subject, string bodyHtml, List<AttachmentDto> attachments)
        {
            var logs = new List<string>();
            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_settings.Username, _settings.Password);
            }
            catch (Exception ex)
            {
                logs.Add("Error conectando SMTP: " + ex.Message);
                return logs;
            }

            foreach (var rawEmail in emails)
            {
                var toEmail = rawEmail?.Trim();
                if (string.IsNullOrWhiteSpace(toEmail)) { logs.Add("Correo vacío omitido"); continue; }
                var msg = new MimeMessage();
                msg.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
                try
                {
                    msg.To.Add(new MailboxAddress(toEmail, toEmail));
                }
                catch (ParseException ex)
                {
                    logs.Add($"Correo inválido omitido {toEmail}: {ex.Message}");
                    continue;
                }
                msg.Subject = subject;

                var builder = new BodyBuilder();
                // Banner opcional
                var bannerPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Plantillas", "img2.jpg");
                //var bannerPath = Path.Combine(AppContext.BaseDirectory, "Plantillas", "img2.jpg");
                if (File.Exists(bannerPath))
                {
                    var bannerImage = builder.LinkedResources.Add(bannerPath);
                    bannerImage.ContentId = MimeUtils.GenerateMessageId();
                    bodyHtml = bodyHtml.Replace("{{img2}}", $"<img src=\"cid:{bannerImage.ContentId}\" style=\"max-width:100%;height:auto;\" />");
                }
                else
                {
                    bodyHtml = bodyHtml.Replace("{{img2}}", "IMG no encontrada");
                }
                builder.HtmlBody = bodyHtml;
                if (attachments != null)
                {
                    foreach (var att in attachments)
                    {
                        builder.Attachments.Add(att.FileName, att.Content, new ContentType("application", "pdf"));
                    }
                }
                msg.Body = builder.ToMessageBody();
                try
                {
                    await client.SendAsync(msg);
                    logs.Add($"✔️ Enviado a {toEmail}");
                }
                catch (Exception ex)
                {
                    logs.Add($"❌ Error {toEmail}: {ex.Message}");
                }
            }
            await client.DisconnectAsync(true);
            return logs;
        }

        public async Task SendEmailWithAttachmentsAsync(
            string toEmail,
            string subject,
            string bodyHtml,
            List<AttachmentDto> attachments)
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            toEmail = toEmail.Trim();
            if (string.IsNullOrWhiteSpace(toEmail))
            {
                throw new ArgumentException("El correo electrónico del destinatario no puede estar vacío.", nameof(toEmail));
            }
            try
            {
                msg.To.Add(new MailboxAddress(toEmail, toEmail));
            }
            catch (MimeKit.ParseException ex)
            {
                Console.WriteLine($"Correo inválido ignorado: {toEmail} - {ex.Message}");
                return; // Sale del método y no intenta enviar el correo
            }
            msg.Subject = subject;

            var builder = new BodyBuilder();

            var logoPath = Path.Combine(AppContext.BaseDirectory, "Templates", "logo.png");
            var bannerPath = Path.Combine(AppContext.BaseDirectory, "Templates", "elecciones.jpg");
            if (File.Exists(bannerPath))
            {
                var bannerImage = builder.LinkedResources.Add(bannerPath);
                bannerImage.ContentId = MimeUtils.GenerateMessageId();
                bodyHtml = bodyHtml.Replace("{{img2}}", $"<img src=\"cid:{bannerImage.ContentId}\" style=\"max-width:100%;height:auto;\" />");
            }
            else
            {
                bodyHtml = bodyHtml.Replace("{{img2}}", "");
            }
            builder.HtmlBody = bodyHtml;

            msg.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, MailKit.Security.SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_settings.Username, _settings.Password);

            try
            {
                await client.SendAsync(msg);
                Console.WriteLine($"Correo enviado con exito a {toEmail}");

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al enviar correo a {toEmail}: {ex.Message}");
            }
            await Task.Delay(1000);
            await client.DisconnectAsync(true);
        }

        public List<DataUsersDto> LeerUsuariosDesdeExcel(string rutaExcel)
        {
            var lista = new List<DataUsersDto>();

            using var workbook = new XLWorkbook(rutaExcel);
            var worksheet = workbook.Worksheet(1); // Primera hoja
            var usedRange = worksheet.RangeUsed();
            if (usedRange == null)
            {
                return lista;
            }
            var rows = usedRange.RowsUsed().Skip(4); // Salta encabezado

            foreach (var row in rows)
            {
                if (row.CellsUsed().Count() >= 3)
                {
                    var usuario = new DataUsersDto
                    {
                        Cedula = row.Cell(1).GetString(),
                        Nombres = row.Cell(2).GetString(),
                        Email = row.Cell(3).GetString()
                    };
                    lista.Add(usuario);
                }
            }
            return lista;
        }

        public List<string> LeerCorreosDesdeTxt(string rutaTxt)
        {
            return EmailFileHelper.LeerCorreosDesdeTxt(rutaTxt);
        }
    }
}
