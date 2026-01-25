using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace GravityCapture.Services
{
    public interface IOcrService
    {
        Task<string> ExtractAsync(Bitmap bitmap, CancellationToken ct = default);
    }
}
