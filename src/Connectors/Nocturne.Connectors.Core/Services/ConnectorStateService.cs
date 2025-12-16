using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services
{
    public class ConnectorStateService : IConnectorStateService
    {
        public ConnectorState CurrentState { get; private set; } = ConnectorState.Idle;
        public string? StateMessage { get; private set; }

        public void SetState(ConnectorState state, string? message = null)
        {
            CurrentState = state;
            StateMessage = message;
        }
    }
}
