namespace GravityCapture.Models
{
    // Extends your existing AppSettings without changing that file.
    public sealed partial class AppSettings
    {
        /// <summary>
        /// If true, use the stage OCR API; if false, use local Tesseract.
        /// </summary>
        public bool UseRemoteOcr { get; set; } = true;
    }
}
