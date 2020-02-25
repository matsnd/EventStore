using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Services.TimerService;
using EventStore.Core.TransactionLog.Checkpoint;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services {
	public enum ElectionsState {
		Idle,
		ElectingLeader,
		Leader,
		Acceptor,
		Shutdown
	}

	public class ElectionsService : IHandle<SystemMessage.BecomeShuttingDown>,
		IHandle<GossipMessage.GossipUpdated>,
		IHandle<ElectionMessage.StartElections>,
		IHandle<ElectionMessage.ElectionsTimedOut>,
		IHandle<ElectionMessage.ViewChange>,
		IHandle<ElectionMessage.ViewChangeProof>,
		IHandle<ElectionMessage.SendViewChangeProof>,
		IHandle<ElectionMessage.Prepare>,
		IHandle<ElectionMessage.PrepareOk>,
		IHandle<ElectionMessage.Proposal>,
		IHandle<ElectionMessage.Accept>,
		IHandle<ClientMessage.SetNodePriority>,
		IHandle<ClientMessage.ResignNode>,
		IHandle<ElectionMessage.LeaderIsResigning>,
		IHandle<ElectionMessage.LeaderIsResigningOk> {
		public static readonly TimeSpan LeaderElectionProgressTimeout = TimeSpan.FromMilliseconds(1000);
		public static readonly TimeSpan SendViewChangeProofInterval = TimeSpan.FromMilliseconds(5000);

		private static readonly ILogger Log = Serilog.Log.ForContext<ElectionsService>();
		private static readonly IPEndPointComparer IPComparer = new IPEndPointComparer();

		private readonly IPublisher _publisher;
		private readonly IEnvelope _publisherEnvelope;
		private readonly VNodeInfo _nodeInfo;
		private readonly int _clusterSize;
		private readonly ICheckpoint _writerCheckpoint;
		private readonly ICheckpoint _chaserCheckpoint;
		private readonly IEpochManager _epochManager;
		private readonly Func<long> _getLastCommitPosition;
		private int _nodePriority;
		private readonly ITimeProvider _timeProvider;

		private int _lastAttemptedView = -1;
		private int _lastInstalledView = -1;
		private ElectionsState _state = ElectionsState.Idle;

		private readonly HashSet<Guid> _vcReceived = new HashSet<Guid>();

		private readonly Dictionary<Guid, ElectionMessage.PrepareOk> _prepareOkReceived =
			new Dictionary<Guid, ElectionMessage.PrepareOk>();

		private readonly HashSet<Guid> _leaderIsResigningOkReceived = new HashSet<Guid>();
		private readonly HashSet<Guid> _acceptsReceived = new HashSet<Guid>();

		private LeaderCandidate _leaderProposal;
		private Guid? _leader;
		private Guid? _lastElectedLeader;

		private MemberInfo[] _servers;
		private Guid? _resigningLeaderInstanceId;

		public ElectionsService(IPublisher publisher,
			VNodeInfo nodeInfo,
			int clusterSize,
			ICheckpoint writerCheckpoint,
			ICheckpoint chaserCheckpoint,
			IEpochManager epochManager,
			Func<long> getLastCommitPosition,
			int nodePriority,
			ITimeProvider timeProvider) {
			Ensure.NotNull(publisher, nameof(publisher));
			Ensure.NotNull(nodeInfo, nameof(nodeInfo));
			Ensure.Positive(clusterSize, nameof(clusterSize));
			Ensure.NotNull(writerCheckpoint, nameof(writerCheckpoint));
			Ensure.NotNull(chaserCheckpoint, nameof(chaserCheckpoint));
			Ensure.NotNull(epochManager, nameof(epochManager));
			Ensure.NotNull(getLastCommitPosition, nameof(getLastCommitPosition));
			Ensure.NotNull(timeProvider, nameof(timeProvider));

			_publisher = publisher;
			_nodeInfo = nodeInfo;
			_publisherEnvelope = new PublishEnvelope(_publisher);
			_clusterSize = clusterSize;
			_writerCheckpoint = writerCheckpoint;
			_chaserCheckpoint = chaserCheckpoint;
			_epochManager = epochManager;
			_getLastCommitPosition = getLastCommitPosition;
			_nodePriority = nodePriority;
			_timeProvider = timeProvider;

			var ownInfo = GetOwnInfo();
			_servers = new[] {
				MemberInfo.ForVNode(nodeInfo.InstanceId,
					_timeProvider.UtcNow,
					VNodeState.Initializing,
					true,
					nodeInfo.InternalTcp, nodeInfo.InternalSecureTcp,
					nodeInfo.ExternalTcp, nodeInfo.ExternalSecureTcp,
					nodeInfo.ExternalHttp,
					ownInfo.LastCommitPosition, ownInfo.WriterCheckpoint, ownInfo.ChaserCheckpoint,
					ownInfo.EpochPosition, ownInfo.EpochNumber, ownInfo.EpochId, ownInfo.NodePriority,
					nodeInfo.IsReadOnlyReplica)
			};
		}

		public void SubscribeMessages(ISubscriber subscriber) {
			subscriber.Subscribe<SystemMessage.BecomeShuttingDown>(this);
			subscriber.Subscribe<GossipMessage.GossipUpdated>(this);
			subscriber.Subscribe<ElectionMessage.StartElections>(this);
			subscriber.Subscribe<ElectionMessage.ElectionsTimedOut>(this);
			subscriber.Subscribe<ElectionMessage.ViewChange>(this);
			subscriber.Subscribe<ElectionMessage.ViewChangeProof>(this);
			subscriber.Subscribe<ElectionMessage.SendViewChangeProof>(this);
			subscriber.Subscribe<ElectionMessage.Prepare>(this);
			subscriber.Subscribe<ElectionMessage.PrepareOk>(this);
			subscriber.Subscribe<ElectionMessage.Proposal>(this);
			subscriber.Subscribe<ElectionMessage.Accept>(this);
			subscriber.Subscribe<ElectionMessage.LeaderIsResigning>(this);
			subscriber.Subscribe<ElectionMessage.LeaderIsResigningOk>(this);
			subscriber.Subscribe<ClientMessage.SetNodePriority>(this);
			subscriber.Subscribe<ClientMessage.ResignNode>(this);
		}

		public void Handle(ClientMessage.SetNodePriority message) {
			Log.Debug("Setting Node Priority to {nodePriority}.", message.NodePriority);
			_nodePriority = message.NodePriority;
			_publisher.Publish(new GossipMessage.UpdateNodePriority(_nodePriority));
		}

		public void Handle(ClientMessage.ResignNode message) {
			if (_leader != null && _nodeInfo.InstanceId == _leader) {
				_resigningLeaderInstanceId = _leader;
				var leaderIsResigningMessageOk = new ElectionMessage.LeaderIsResigningOk(
					_nodeInfo.InstanceId,
					_nodeInfo.ExternalHttp,
					_nodeInfo.InstanceId,
					_nodeInfo.ExternalHttp);
				_leaderIsResigningOkReceived.Clear();
				Handle(leaderIsResigningMessageOk);
				SendToAllExceptMe(new ElectionMessage.LeaderIsResigning(
					_nodeInfo.InstanceId, _nodeInfo.ExternalHttp));
			} else {
				Log.Information("ELECTIONS: ONLY LEADER RESIGNATION IS SUPPORTED AT THE MOMENT. IGNORING RESIGNATION.");
			}
		}

		public void Handle(ElectionMessage.LeaderIsResigning message) {
			if (_nodeInfo.IsReadOnlyReplica) {
				Log.Debug(
					"ELECTIONS: THIS NODE IS A READ ONLY REPLICA. IT IS NOT ALLOWED TO VOTE AND THEREFORE NOT ALLOWED TO ACKNOWLEDGE LEADER RESIGNATION.");
				return;
			}

			Log.Debug("ELECTIONS: LEADER IS RESIGNING [{leaderExternalHttp}, {leaderId:B}].",
				message.LeaderExternalHttp, message.LeaderId);
			var leaderIsResigningMessageOk = new ElectionMessage.LeaderIsResigningOk(
				message.LeaderId,
				message.LeaderExternalHttp,
				_nodeInfo.InstanceId,
				_nodeInfo.ExternalHttp);

			_resigningLeaderInstanceId = message.LeaderId;
			_publisher.Publish(new GrpcMessage.SendOverGrpc(message.LeaderExternalHttp, leaderIsResigningMessageOk,
				_timeProvider.LocalTime.Add(LeaderElectionProgressTimeout)));
		}

		public void Handle(ElectionMessage.LeaderIsResigningOk message) {
			Log.Debug(
				"ELECTIONS: LEADER IS RESIGNING OK FROM [{serverExternalHttp},{serverId:B}] M=[{leaderExternalHttp},{leaderId:B}]).",
				message.ServerExternalHttp,
				message.ServerId,
				message.LeaderExternalHttp,
				message.LeaderId);
			if (_leaderIsResigningOkReceived.Add(message.ServerId) &&
					_leaderIsResigningOkReceived.Count == _clusterSize / 2 + 1) {
				Log.Debug(
					"ELECTIONS: MAJORITY OF ACCEPTANCE OF RESIGNATION OF LEADER [{leaderExternalHttp},{leaderId:B}]. NOW INITIATING LEADER RESIGNATION.",
					message.LeaderExternalHttp, message.LeaderId);
				_publisher.Publish(new SystemMessage.InitiateLeaderResignation());
			}
		}

		public void Handle(SystemMessage.BecomeShuttingDown message) {
			_state = ElectionsState.Shutdown;
		}

		public void Handle(GossipMessage.GossipUpdated message) {
			_servers = message.ClusterInfo.Members.Where(x => x.State != VNodeState.Manager)
				.Where(x => x.IsAlive)
				.OrderByDescending(x => x.ExternalHttpEndPoint, IPComparer)
				.ToArray();
		}

		public void Handle(ElectionMessage.StartElections message) {
			if (_state == ElectionsState.Shutdown) return;
			if (_state == ElectionsState.ElectingLeader) return;

			if (_nodeInfo.IsReadOnlyReplica)
				Log.Verbose("ELECTIONS: THIS NODE IS A READ ONLY REPLICA.");

			Log.Debug("ELECTIONS: STARTING ELECTIONS.");
			ShiftToLeaderElection(_lastAttemptedView + 1);
			_publisher.Publish(TimerMessage.Schedule.Create(SendViewChangeProofInterval,
				_publisherEnvelope,
				new ElectionMessage.SendViewChangeProof()));
		}

		public void Handle(ElectionMessage.ElectionsTimedOut message) {
			if (_state == ElectionsState.Shutdown) return;
			if (message.View != _lastAttemptedView) return;
			// we are still on the same view, but we selected leader
			if (_state != ElectionsState.ElectingLeader && _leader != null) return;

			Log.Debug("ELECTIONS: (V={view}) TIMED OUT! (S={state}, M={leader}).", message.View, _state, _leader);
			ShiftToLeaderElection(_lastAttemptedView + 1);
		}

		private void ShiftToLeaderElection(int view) {
			Log.Debug("ELECTIONS: (V={view}) SHIFT TO LEADER ELECTION.", view);

			_state = ElectionsState.ElectingLeader;
			_vcReceived.Clear();
			_prepareOkReceived.Clear();
			_lastAttemptedView = view;

			_leaderProposal = null;
			_leader = null;
			_acceptsReceived.Clear();

			var viewChangeMsg = new ElectionMessage.ViewChange(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp, view);
			Handle(viewChangeMsg);
			SendToAllExceptMe(viewChangeMsg);
			_publisher.Publish(TimerMessage.Schedule.Create(LeaderElectionProgressTimeout,
				_publisherEnvelope,
				new ElectionMessage.ElectionsTimedOut(view)));
		}

		private void SendToAllExceptMe(Message message) {
			foreach (var server in _servers.Where(x => x.InstanceId != _nodeInfo.InstanceId)) {
				_publisher.Publish(new GrpcMessage.SendOverGrpc(server.ExternalHttpEndPoint, message,
					_timeProvider.LocalTime.Add(LeaderElectionProgressTimeout)));
			}
		}

		public void Handle(ElectionMessage.ViewChange message) {
			if (_state == ElectionsState.Shutdown) return;
			if (_state == ElectionsState.Idle) return;

			if (message.AttemptedView <= _lastInstalledView) return;

			Log.Debug("ELECTIONS: (V={view}) VIEWCHANGE FROM [{serverExternalHttp}, {serverId:B}].",
				message.AttemptedView, message.ServerExternalHttp, message.ServerId);

			if (message.AttemptedView > _lastAttemptedView)
				ShiftToLeaderElection(message.AttemptedView);

			if (_vcReceived.Add(message.ServerId) && _vcReceived.Count == _clusterSize / 2 + 1) {
				Log.Debug("ELECTIONS: (V={view}) MAJORITY OF VIEWCHANGE.", message.AttemptedView);
				if (AmILeaderOf(_lastAttemptedView))
					ShiftToPreparePhase();
			}
		}

		public void Handle(ElectionMessage.SendViewChangeProof message) {
			if (_state == ElectionsState.Shutdown) return;

			if (_lastInstalledView >= 0)
				SendToAllExceptMe(new ElectionMessage.ViewChangeProof(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp,
					_lastInstalledView));

			_publisher.Publish(TimerMessage.Schedule.Create(SendViewChangeProofInterval,
				_publisherEnvelope,
				new ElectionMessage.SendViewChangeProof()));
		}

		public void Handle(ElectionMessage.ViewChangeProof message) {
			if (_state == ElectionsState.Shutdown) return;
			if (_state == ElectionsState.Idle) return;
			if (message.InstalledView <= _lastInstalledView) return;

			_lastAttemptedView = message.InstalledView;

			_publisher.Publish(TimerMessage.Schedule.Create(LeaderElectionProgressTimeout,
				_publisherEnvelope,
				new ElectionMessage.ElectionsTimedOut(_lastAttemptedView)));

			if (AmILeaderOf(_lastAttemptedView)) {
				Log.Debug(
					"ELECTIONS: (IV={installedView}) VIEWCHANGEPROOF FROM [{serverExternalHttp}, {serverId:B}]. JUMPING TO LEADER STATE.",
					message.InstalledView, message.ServerExternalHttp, message.ServerId);

				ShiftToPreparePhase();
			} else {
				Log.Debug(
					"ELECTIONS: (IV={installedView}) VIEWCHANGEPROOF FROM [{serverExternalHttp}, {serverId:B}]. JUMPING TO NON-LEADER STATE.",
					message.InstalledView, message.ServerExternalHttp, message.ServerId);

				ShiftToAcceptor();
			}
		}

		private bool AmILeaderOf(int lastAttemptedView) {
			var serversExcludingNonPotentialLeaders = _servers.Where(x => !x.IsReadOnlyReplica).ToArray();
			var leader =
				serversExcludingNonPotentialLeaders[lastAttemptedView % serversExcludingNonPotentialLeaders.Length];
			return leader.InstanceId == _nodeInfo.InstanceId;
		}

		private void ShiftToPreparePhase() {
			Log.Debug("ELECTIONS: (V={lastAttemptedView}) SHIFT TO PREPARE PHASE.", _lastAttemptedView);

			_lastInstalledView = _lastAttemptedView;
			_prepareOkReceived.Clear();

			Handle(CreatePrepareOk(_lastInstalledView));
			SendToAllExceptMe(new ElectionMessage.Prepare(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp,
				_lastInstalledView));
		}

		public void Handle(ElectionMessage.Prepare message) {
			if (_state == ElectionsState.Shutdown) return;
			if (message.ServerId == _nodeInfo.InstanceId) return;
			if (message.View != _lastAttemptedView) return;
			if (_servers.All(x => x.InstanceId != message.ServerId)) return; // unknown instance

			Log.Debug("ELECTIONS: (V={lastAttemptedView}) PREPARE FROM [{serverExternalHttp}, {serverId:B}].",
				_lastAttemptedView, message.ServerExternalHttp, message.ServerId);

			if (_state == ElectionsState.ElectingLeader) // install the view
				ShiftToAcceptor();

			if (_nodeInfo.IsReadOnlyReplica) {
				Log.Information("ELECTIONS: READ ONLY REPLICA, NOT ACCEPTING PREPARE, NOT ELIGIBLE TO VOTE [{0}]",
					_nodeInfo.ExternalHttp);
				return;
			}

			var prepareOk = CreatePrepareOk(message.View);
			_publisher.Publish(new GrpcMessage.SendOverGrpc(message.ServerExternalHttp, prepareOk,
				_timeProvider.LocalTime.Add(LeaderElectionProgressTimeout)));
		}

		private ElectionMessage.PrepareOk CreatePrepareOk(int view) {
			var ownInfo = GetOwnInfo();
			return new ElectionMessage.PrepareOk(view, ownInfo.InstanceId, ownInfo.ExternalHttp,
				ownInfo.EpochNumber, ownInfo.EpochPosition, ownInfo.EpochId,
				ownInfo.LastCommitPosition, ownInfo.WriterCheckpoint, ownInfo.ChaserCheckpoint,
				ownInfo.NodePriority);
		}

		private void ShiftToAcceptor() {
			Log.Debug("ELECTIONS: (V={lastAttemptedView}) SHIFT TO REG_ACCEPTOR.", _lastAttemptedView);

			_state = ElectionsState.Acceptor;
			_lastInstalledView = _lastAttemptedView;
		}

		public void Handle(ElectionMessage.PrepareOk msg) {
			if (_state == ElectionsState.Shutdown) return;
			if (_state != ElectionsState.ElectingLeader) return;
			if (msg.View != _lastAttemptedView) return;

			Log.Debug("ELECTIONS: (V={view}) PREPARE_OK FROM {nodeInfo}.", msg.View,
				FormatNodeInfo(msg.ServerExternalHttp, msg.ServerId,
					msg.LastCommitPosition, msg.WriterCheckpoint, msg.ChaserCheckpoint,
					msg.EpochNumber, msg.EpochPosition, msg.EpochId, msg.NodePriority));

			if (!_prepareOkReceived.ContainsKey(msg.ServerId)) {
				_prepareOkReceived.Add(msg.ServerId, msg);
				if (_prepareOkReceived.Count == _clusterSize / 2 + 1)
					ShiftToLeader();
			}
		}

		private void ShiftToLeader() {
			if (_nodeInfo.IsReadOnlyReplica) {
				Log.Debug("ELECTIONS: (V={lastAttemptedView}) NOT SHIFTING TO REG_LEADER AS I'M READONLY.",
					_lastAttemptedView);
				return;
			}

			Log.Debug("ELECTIONS: (V={lastAttemptedView}) SHIFT TO REG_LEADER.", _lastAttemptedView);

			_state = ElectionsState.Leader;
			SendProposal();
		}

		private void SendProposal() {
			_acceptsReceived.Clear();
			_leaderProposal = null;

			var leader = GetBestLeaderCandidate(_prepareOkReceived, _servers, _lastElectedLeader,
				_resigningLeaderInstanceId);
			if (leader == null) {
				Log.Verbose("ELECTIONS: (V={lastAttemptedView}) NO LEADER CANDIDATE WHEN TRYING TO SEND PROPOSAL.",
					_lastAttemptedView);
				return;
			}

			_leaderProposal = leader;

			Log.Debug("ELECTIONS: (V={lastAttemptedView}) SENDING PROPOSAL CANDIDATE: {formatNodeInfo}, ME: {ownInfo}.",
				_lastAttemptedView, FormatNodeInfo(leader), FormatNodeInfo(GetOwnInfo()));

			var proposal = new ElectionMessage.Proposal(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp,
				leader.InstanceId, leader.ExternalHttp,
				_lastInstalledView,
				leader.EpochNumber, leader.EpochPosition, leader.EpochId,
				leader.LastCommitPosition, leader.WriterCheckpoint, leader.ChaserCheckpoint, leader.NodePriority);
			Handle(new ElectionMessage.Accept(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp,
				leader.InstanceId, leader.ExternalHttp, _lastInstalledView));
			SendToAllExceptMe(proposal);
		}

		public static LeaderCandidate GetBestLeaderCandidate(Dictionary<Guid, ElectionMessage.PrepareOk> received,
			MemberInfo[] servers, Guid? lastElectedLeader, Guid? resigningLeaderInstanceId) {
			if (lastElectedLeader.HasValue && lastElectedLeader.Value != resigningLeaderInstanceId) {
				if (received.TryGetValue(lastElectedLeader.Value, out var leaderMsg)) {
					return new LeaderCandidate(leaderMsg.ServerId, leaderMsg.ServerExternalHttp,
						leaderMsg.EpochNumber, leaderMsg.EpochPosition, leaderMsg.EpochId,
						leaderMsg.LastCommitPosition, leaderMsg.WriterCheckpoint, leaderMsg.ChaserCheckpoint,
						leaderMsg.NodePriority);
				}

				var leader = servers.FirstOrDefault(x =>
					x.IsAlive && x.InstanceId == lastElectedLeader && x.State == VNodeState.Leader);
				if (leader != null) {
					return new LeaderCandidate(leader.InstanceId, leader.ExternalHttpEndPoint,
						leader.EpochNumber, leader.EpochPosition, leader.EpochId,
						leader.LastCommitPosition, leader.WriterCheckpoint, leader.ChaserCheckpoint,
						leader.NodePriority);
				}
			}

			var best = received.Values
				.OrderByDescending(x => x.EpochNumber)
				.ThenByDescending(x => x.LastCommitPosition)
				.ThenByDescending(x => x.WriterCheckpoint)
				.ThenByDescending(x => x.ChaserCheckpoint)
				.ThenByDescending(x => x.NodePriority)
				.ThenByDescending(x => x.ServerId)
				.FirstOrDefault();
			if (best == null)
				return null;
			return new LeaderCandidate(best.ServerId, best.ServerExternalHttp,
				best.EpochNumber, best.EpochPosition, best.EpochId,
				best.LastCommitPosition, best.WriterCheckpoint, best.ChaserCheckpoint, best.NodePriority);
		}

		public static bool IsLegitimateLeader(int view, IPEndPoint proposingServerEndPoint, Guid proposingServerId,
			LeaderCandidate candidate, MemberInfo[] servers, Guid? lastElectedLeader, VNodeInfo nodeInfo,
			LeaderCandidate ownInfo,
			Guid? resigningLeader) {
			var leader = servers.FirstOrDefault(x =>
				x.IsAlive && x.InstanceId == lastElectedLeader && x.State == VNodeState.Leader);

			if (leader != null && leader.InstanceId != resigningLeader) {
				if (candidate.InstanceId == leader.InstanceId
				    || candidate.EpochNumber > leader.EpochNumber
				    || (candidate.EpochNumber == leader.EpochNumber && candidate.EpochId != leader.EpochId))
					return true;

				Log.Debug(
					"ELECTIONS: (V={view}) NOT LEGITIMATE LEADER PROPOSAL FROM [{proposingServerEndPoint},{proposingServerId:B}] M={candidateInfo}. "
					+ "PREVIOUS LEADER IS ALIVE: [{leaderExternalHttp},{leaderId:B}].",
					view, proposingServerEndPoint, proposingServerId, FormatNodeInfo(candidate),
					leader.ExternalHttpEndPoint, leader.InstanceId);
				return false;
			}

			if (candidate.InstanceId == nodeInfo.InstanceId)
				return true;

			if (!IsCandidateGoodEnough(candidate, ownInfo)) {
				Log.Debug(
					"ELECTIONS: (V={view}) NOT LEGITIMATE LEADER PROPOSAL FROM [{proposingServerEndPoint},{proposingServerId:B}] M={candidateInfo}. ME={ownInfo}.",
					view, proposingServerEndPoint, proposingServerId, FormatNodeInfo(candidate),
					FormatNodeInfo(ownInfo));
				return false;
			}

			return true;
		}

		private static bool IsCandidateGoodEnough(LeaderCandidate candidate, LeaderCandidate ownInfo) {
			if (candidate.EpochNumber != ownInfo.EpochNumber)
				return candidate.EpochNumber > ownInfo.EpochNumber;
			if (candidate.LastCommitPosition != ownInfo.LastCommitPosition)
				return candidate.LastCommitPosition > ownInfo.LastCommitPosition;
			if (candidate.WriterCheckpoint != ownInfo.WriterCheckpoint)
				return candidate.WriterCheckpoint > ownInfo.WriterCheckpoint;
			if (candidate.ChaserCheckpoint != ownInfo.ChaserCheckpoint)
				return candidate.ChaserCheckpoint > ownInfo.ChaserCheckpoint;
			return true;
		}

		public void Handle(ElectionMessage.Proposal message) {
			if (_state == ElectionsState.Shutdown) return;
			if (message.ServerId == _nodeInfo.InstanceId) return;
			if (_state != ElectionsState.Acceptor) return;
			if (message.View != _lastInstalledView) return;
			if (_servers.All(x => x.InstanceId != message.ServerId)) return;
			if (_servers.All(x => x.InstanceId != message.LeaderId)) return;

			var candidate = new LeaderCandidate(message.LeaderId, message.LeaderExternalHttp,
				message.EpochNumber, message.EpochPosition, message.EpochId,
				message.LastCommitPosition, message.WriterCheckpoint, message.ChaserCheckpoint, message.NodePriority);

			var ownInfo = GetOwnInfo();
			if (!IsLegitimateLeader(message.View, message.ServerExternalHttp, message.ServerId,
				candidate, _servers, _lastElectedLeader, _nodeInfo, ownInfo,
				_resigningLeaderInstanceId))
				return;

			Log.Debug(
				"ELECTIONS: (V={lastAttemptedView}) PROPOSAL FROM [{serverExternalHttp},{serverId:B}] M={candidateInfo}. ME={ownInfo}, NodePriority={priority}",
				_lastAttemptedView,
				message.ServerExternalHttp, message.ServerId, FormatNodeInfo(candidate), FormatNodeInfo(GetOwnInfo()),
				message.NodePriority);

			if (_leaderProposal == null) {
				_leaderProposal = candidate;
				_acceptsReceived.Clear();
			}

			if (_leaderProposal.InstanceId == message.LeaderId) {
				// NOTE: proposal from other server is also implicit Accept from that server
				Handle(new ElectionMessage.Accept(message.ServerId, message.ServerExternalHttp,
					message.LeaderId, message.LeaderExternalHttp, message.View));
				var accept = new ElectionMessage.Accept(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp,
					message.LeaderId, message.LeaderExternalHttp, message.View);
				Handle(accept); // implicitly sent accept to ourselves
				SendToAllExceptMe(accept);
			}
		}

		public void Handle(ElectionMessage.Accept message) {
			if (_state == ElectionsState.Shutdown) return;
			if (message.View != _lastInstalledView) return;
			if (_leaderProposal == null) return;
			if (_leaderProposal.InstanceId != message.LeaderId) return;

			Log.Debug(
				"ELECTIONS: (V={view}) ACCEPT FROM [{serverExternalHttp},{serverId:B}] M=[{leaderExternalHttp},{leaderId:B}]).",
				message.View,
				message.ServerExternalHttp, message.ServerId, message.LeaderExternalHttp, message.LeaderId);

			if (_acceptsReceived.Add(message.ServerId) && _acceptsReceived.Count == _clusterSize / 2 + 1) {
				var leader = _servers.FirstOrDefault(x => x.InstanceId == _leaderProposal.InstanceId);
				if (leader != null) {
					_leader = _leaderProposal.InstanceId;
					Log.Information("ELECTIONS: (V={view}) DONE. ELECTED LEADER = {leaderInfo}. ME={ownInfo}.", message.View,
						FormatNodeInfo(_leaderProposal), FormatNodeInfo(GetOwnInfo()));
					_lastElectedLeader = _leader;
					_resigningLeaderInstanceId = null;
					_publisher.Publish(new ElectionMessage.ElectionsDone(message.View, leader));
				}
			}
		}

		private LeaderCandidate GetOwnInfo() {
			var lastEpoch = _epochManager.GetLastEpoch();
			var writerCheckpoint = _writerCheckpoint.Read();
			var chaserCheckpoint = _chaserCheckpoint.Read();
			var lastCommitPosition = _getLastCommitPosition();
			return new LeaderCandidate(_nodeInfo.InstanceId, _nodeInfo.ExternalHttp,
				lastEpoch == null ? -1 : lastEpoch.EpochNumber,
				lastEpoch == null ? -1 : lastEpoch.EpochPosition,
				lastEpoch == null ? Guid.Empty : lastEpoch.EpochId,
				lastCommitPosition, writerCheckpoint, chaserCheckpoint, _nodePriority);
		}

		private static string FormatNodeInfo(LeaderCandidate candidate) {
			return FormatNodeInfo(candidate.ExternalHttp, candidate.InstanceId,
				candidate.LastCommitPosition, candidate.WriterCheckpoint, candidate.ChaserCheckpoint,
				candidate.EpochNumber, candidate.EpochPosition, candidate.EpochId, candidate.NodePriority);
		}

		private static string FormatNodeInfo(IPEndPoint serverEndPoint, Guid serverId,
			long lastCommitPosition, long writerCheckpoint, long chaserCheckpoint,
			int epochNumber, long epochPosition, Guid epochId, int priority) {
			return string.Format("[{0},{1:B}](L={2},W={3},C={4},E{5}@{6}:{7:B},Priority={8})",
				serverEndPoint, serverId,
				lastCommitPosition, writerCheckpoint, chaserCheckpoint,
				epochNumber, epochPosition, epochId, priority);
		}

		public class LeaderCandidate {
			public readonly Guid InstanceId;
			public readonly IPEndPoint ExternalHttp;

			public readonly int EpochNumber;
			public readonly long EpochPosition;
			public readonly Guid EpochId;

			public readonly long LastCommitPosition;
			public readonly long WriterCheckpoint;
			public readonly long ChaserCheckpoint;

			public readonly int NodePriority;

			public LeaderCandidate(Guid instanceId, IPEndPoint externalHttp,
				int epochNumber, long epochPosition, Guid epochId,
				long lastCommitPosition, long writerCheckpoint, long chaserCheckpoint,
				int nodePriority) {
				InstanceId = instanceId;
				ExternalHttp = externalHttp;
				EpochNumber = epochNumber;
				EpochPosition = epochPosition;
				EpochId = epochId;
				LastCommitPosition = lastCommitPosition;
				WriterCheckpoint = writerCheckpoint;
				ChaserCheckpoint = chaserCheckpoint;
				NodePriority = nodePriority;
			}
		}
	}
}
