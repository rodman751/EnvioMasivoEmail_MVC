using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Servicios.DTOs
{
    public class AttachmentDto
    {
        // Ideally, a file name can be null if not loaded yet
        public string? FileName { get; set; }

        // Initialize byte array to empty so it's never null
        public byte[] Content { get; set; } = Array.Empty<byte>();
    }
}