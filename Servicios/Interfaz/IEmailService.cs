using Servicios.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Servicios.Interfaz
{
    public interface IEmailService
    {

        Task SendEmailWithAttachmentsAsync(
            string toEmail,
            string subject,
            string bodyHtml,
            List<AttachmentDto> attachments);
        //Task SendBulkEmailAsync(
        //IEnumerable<EmailRecipient> recipients,
        //string templatePath,
        //object modelCommon);

        List<string> LeerCorreosDesdeTxt(string rutaTxt);

        // Nuevo método eficiente para envío masivo usando una sola conexión SMTP
        Task<List<string>> SendBulkEmailsAsync(
            IEnumerable<string> emails, 
            string subject, 
            string bodyHtml, 
            List<AttachmentDto>? attachments // <--- Agrega el '?'
        );
    }
}
