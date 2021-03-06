using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Messaging;
using EventStore.Client.Projections;
using EventStore.Projections.Core.Messages;
using Grpc.Core;

namespace EventStore.Projections.Core.Services.Grpc {
	public partial class ProjectionManagement {
		public override async Task<DeleteResp> Delete(DeleteReq request, ServerCallContext context) {
			var deletedSource = new TaskCompletionSource<bool>();
			var options = request.Options;

			var user = context.GetHttpContext().User;

			var name = options.Name;
			var deleteCheckpointStream = options.DeleteCheckpointStream;
			var deleteStateStream = options.DeleteStateStream;
			var deleteEmittedStreams = options.DeleteEmittedStreams;
			var runAs = new ProjectionManagementMessage.RunAs(user);

			var envelope = new CallbackEnvelope(OnMessage);

			_queue.Publish(new ProjectionManagementMessage.Command.Delete(envelope, name, runAs,
				deleteCheckpointStream, deleteStateStream, deleteEmittedStreams));

			await deletedSource.Task.ConfigureAwait(false);

			return new DeleteResp();

			void OnMessage(Message message) {
				if (!(message is ProjectionManagementMessage.Updated)) {
					deletedSource.TrySetException(UnknownMessage<ProjectionManagementMessage.Updated>(message));
					return;
				}

				deletedSource.TrySetResult(true);
			}
		}
	}
}
