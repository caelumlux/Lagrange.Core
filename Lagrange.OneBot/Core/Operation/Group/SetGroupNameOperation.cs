using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface.Api;
using Lagrange.OneBot.Core.Entity.Action;

namespace Lagrange.OneBot.Core.Operation.Group;

[Operation("set_group_name")]
public class SetGroupNameOperation : IOperation
{
    public async Task<OneBotResult> HandleOperation(BotContext context, JsonObject? payload)
    {
        var message = payload.Deserialize<OneBotSetGroupName>();

        if (message != null)
        {
            bool _ = await context.RenameGroup(message.GroupId, message.GroupName);
            return new OneBotResult(null, 0, "ok");
        }

        throw new Exception();
    }
}