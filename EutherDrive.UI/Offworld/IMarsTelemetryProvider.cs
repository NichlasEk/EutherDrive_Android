using System.Threading;
using System.Threading.Tasks;

namespace EutherDrive.UI.Offworld;

internal interface IMarsTelemetryProvider
{
    Task<MarsTelemetrySnapshot> GetLatestMarsTelemetryAsync(CancellationToken cancellationToken = default);
}
