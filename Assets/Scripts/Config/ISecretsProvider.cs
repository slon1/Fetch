using System.Threading;
using Cysharp.Threading.Tasks;

namespace WebRtcV2.Config
{
    /// <summary>
    /// Provides runtime secrets (TURN credentials, auth tokens).
    /// Implementation is swapped between dev (local file) and production (Worker endpoint).
    /// </summary>
    public interface ISecretsProvider
    {
        UniTask<TurnCredentials> GetTurnCredentialsAsync(CancellationToken ct = default);
    }
}
