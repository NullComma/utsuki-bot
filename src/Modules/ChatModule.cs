using App.Services;
using Discord.Interactions;

namespace App.Modules {
	public class ChatModule : InteractionModuleBase<SocketInteractionContext> {
		public ChatModule(ChatService service) {
			_service = service;
		}

		readonly ChatService _service;
	}
}
