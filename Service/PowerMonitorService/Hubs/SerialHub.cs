using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace PowerMonitorService.Hubs
{
    public class SerialHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
        }
    }
}
