using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using ProjectDawn.Navigation;
using ProjectDawn.Navigation.Hybrid;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.AI;

namespace MultiplayerARPG
{
    /// <summary>
    /// This one is simple client-authoritative entity movement
    /// Being made to show how to simply implements entity movements by using `LiteNetLibTransform`
    /// </summary>
    [RequireComponent(typeof(AgentAuthoring))]
    [RequireComponent(typeof(AgentNavMeshAuthoring))]
    [RequireComponent(typeof(AgentCylinderShapeAuthoring))]
    [RequireComponent(typeof(LiteNetLibTransform))]
    public class SimpleAgentsNavigationEntityMovement : BaseNetworkedGameEntityComponent<BaseGameEntity>, IEntityMovementComponent
    {
        protected const float MIN_MAGNITUDE_TO_DETERMINE_MOVING = 0.01f;
        protected const float MIN_DIRECTION_SQR_MAGNITUDE = 0.0001f;
        protected const float MIN_DISTANCE_TO_TELEPORT = 0.1f;
        protected static readonly ProfilerMarker s_UpdateProfilerMarker = new ProfilerMarker("SimpleNavMeshEntityMovement - Update");

        [Header("Dashing")]
        public EntityMovementForceApplierData dashingForceApplier = EntityMovementForceApplierData.CreateDefault();

        public LiteNetLibTransform CacheNetworkedTransform { get; protected set; }
        public AgentAuthoring CacheAgent { get; private set; }
        public AgentNavMeshAuthoring CacheAgentNavMesh { get; private set; }
        public AgentCylinderShapeAuthoring CacheAgentShape { get; private set; }
        public IEntityTeleportPreparer TeleportPreparer { get; protected set; }
        public bool IsPreparingToTeleport { get { return TeleportPreparer != null && TeleportPreparer.IsPreparingToTeleport; } }
        public float StoppingDistance
        {
            get { return CacheAgent.EntityLocomotion.StoppingDistance; }
        }
        public MovementState MovementState { get; protected set; }
        public ExtraMovementState ExtraMovementState { get; protected set; }
        public DirectionVector2 Direction2D { get { return Vector2.down; } set { } }
        public float CurrentMoveSpeed { get { return CacheAgent.EntityBody.IsStopped ? 0f : CacheAgent.EntityBody.Speed; } }

        // Input codes
        protected bool _isDashing;
        protected Vector3 _inputDirection;
        protected ExtraMovementState _tempExtraMovementState;

        // Teleportation
        protected MovementTeleportState _serverTeleportState;
        protected MovementTeleportState _clientTeleportState;

        // Force simulation
        protected readonly List<EntityMovementForceApplier> _movementForceAppliers = new List<EntityMovementForceApplier>();
        protected IEntityMovementForceUpdateListener[] _forceUpdateListeners;

        // Turn simulate codes
        protected bool _lookRotationApplied;
        protected float _yAngle;
        protected float _targetYAngle;
        protected float _yTurnSpeed;

        // Interpolation Data
        protected SortedList<uint, System.ValueTuple<MovementState, ExtraMovementState>> _interpExtra = new SortedList<uint, System.ValueTuple<MovementState, ExtraMovementState>>();

        public override void EntityAwake()
        {
            // Prepare nav mesh agent component
            CacheNetworkedTransform = gameObject.GetOrAddComponent<LiteNetLibTransform>();
            CacheAgent = gameObject.GetOrAddComponent<AgentAuthoring>();
            CacheAgentNavMesh = gameObject.GetOrAddComponent<AgentNavMeshAuthoring>();
            CacheAgentShape = gameObject.GetOrAddComponent<AgentCylinderShapeAuthoring>();
            TeleportPreparer = gameObject.GetComponent<IEntityTeleportPreparer>();
            _forceUpdateListeners = gameObject.GetComponents<IEntityMovementForceUpdateListener>();
            Rigidbody rigidBody = gameObject.GetComponent<Rigidbody>();
            if (rigidBody != null)
            {
                rigidBody.useGravity = false;
                rigidBody.isKinematic = true;
            }
            // Setup
            CacheNetworkedTransform.syncByOwnerClient = true;
            CacheNetworkedTransform.onWriteSyncBuffer += CacheNetworkedTransform_onWriteSyncBuffer;
            CacheNetworkedTransform.onReadInterpBuffer += CacheNetworkedTransform_onReadInterpBuffer;
            CacheNetworkedTransform.onInterpolate += CacheNetworkedTransform_onInterpolate;
            CacheAgent.enabled = false;
            _yAngle = _targetYAngle = EntityTransform.eulerAngles.y;
            _lookRotationApplied = true;
        }

        public override void EntityStart()
        {
            _clientTeleportState = MovementTeleportState.Responding;
        }

        public override void EntityOnDestroy()
        {
            CacheNetworkedTransform.onWriteSyncBuffer -= CacheNetworkedTransform_onWriteSyncBuffer;
            CacheNetworkedTransform.onReadInterpBuffer -= CacheNetworkedTransform_onReadInterpBuffer;
            CacheNetworkedTransform.onInterpolate -= CacheNetworkedTransform_onInterpolate;
        }

        public override void OnSetOwnerClient(bool isOwnerClient)
        {
            CacheAgent.enabled = CanSimulateMovement();
        }

        public override void ComponentOnEnable()
        {
            CacheAgent.enabled = CanSimulateMovement();
        }

        public override void ComponentOnDisable()
        {
            CacheAgent.enabled = false;
        }

        public bool CanSimulateMovement()
        {
            return Entity.IsOwnerClientOrOwnedByServer;
        }

        protected void CacheNetworkedTransform_onWriteSyncBuffer(NetDataWriter writer, uint tick)
        {
            writer.Put((byte)MovementState);
            writer.Put((byte)ExtraMovementState);
        }

        protected void CacheNetworkedTransform_onReadInterpBuffer(NetDataReader reader, uint tick)
        {
            _interpExtra[tick] = new System.ValueTuple<MovementState, ExtraMovementState>(
                (MovementState)reader.GetByte(),
                (ExtraMovementState)reader.GetByte());
            while (_interpExtra.Count > 30)
            {
                _interpExtra.RemoveAt(0);
            }
        }

        protected void CacheNetworkedTransform_onInterpolate(LiteNetLibTransform.TransformData interpFromData, LiteNetLibTransform.TransformData interpToData, float interpTime)
        {
            if (interpTime <= 0.75f)
            {
                if (_interpExtra.TryGetValue(interpFromData.Tick, out var states))
                {
                    MovementState = states.Item1;
                    ExtraMovementState = states.Item2;
                }
            }
            else
            {
                if (_interpExtra.TryGetValue(interpToData.Tick, out var states))
                {
                    MovementState = states.Item1;
                    ExtraMovementState = states.Item2;
                }
            }
        }

        public void ApplyForce(ApplyMovementForceMode mode, Vector3 direction, ApplyMovementForceSourceType sourceType, int sourceDataId, int sourceLevel, float force, float deceleration, float duration)
        {
            if (!IsServer)
                return;
            if (mode.IsReplaceMovement())
            {
                // Can have only one replace movement force applier, so remove stored ones
                _movementForceAppliers.RemoveReplaceMovementForces();
            }
            _movementForceAppliers.Add(new EntityMovementForceApplier()
                .Apply(mode, direction, sourceType, sourceDataId, sourceLevel, force, deceleration, duration));
        }

        public EntityMovementForceApplier FindForceByActionKey(ApplyMovementForceSourceType sourceType, int sourceDataId)
        {
            return _movementForceAppliers.FindBySource(sourceType, sourceDataId);
        }

        public void ClearAllForces()
        {
            if (!IsServer)
                return;
            _movementForceAppliers.Clear();
        }

        public bool FindGroundedPosition(Vector3 fromPosition, float findDistance, out Vector3 result)
        {
            result = fromPosition;
            float findDist = 1f;
            NavMeshHit navHit;
            while (!NavMesh.SamplePosition(fromPosition, out navHit, findDist, NavMesh.AllAreas))
            {
                findDist += 1f;
                if (findDist > findDistance)
                    return false;
            }
            result = navHit.position;
            return true;
        }

        public Bounds GetMovementBounds()
        {
            Vector3 agentPosition = transform.position;
            Vector3 lossyScale = transform.lossyScale;

            // Calculate the scaled extents using lossy scale
            float scaledRadius = CacheAgentShape.EntityShape.Radius * Mathf.Max(lossyScale.x, lossyScale.z);
            float scaledHeight = CacheAgentShape.EntityShape.Height * lossyScale.y;

            // Adjust the center to include the scale
            Vector3 center = new Vector3(agentPosition.x, agentPosition.y + (scaledHeight * 0.5f), agentPosition.z);
            Vector3 size = new Vector3(scaledRadius * 2, scaledHeight, scaledRadius * 2);
            return new Bounds(center, size);
        }

        protected void SetMovePaths(Vector3 position)
        {
            if (!Entity.CanMove())
                return;
            _inputDirection = Vector3.zero;
            CacheAgent.SetDestinationDeferred(position);
        }

        public void SetSmoothTurnSpeed(float turnDuration)
        {
            _yTurnSpeed = turnDuration;
        }

        public float GetSmoothTurnSpeed()
        {
            return _yTurnSpeed;
        }

        public void KeyMovement(Vector3 moveDirection, MovementState movementState)
        {
            if (!Entity.CanMove())
                return;
            if (!Entity.IsOwnerClientOrOwnedByServer)
                return;
            _inputDirection = moveDirection;
            if (!_isDashing)
                _isDashing = movementState.Has(MovementState.IsDash);
        }

        public void PointClickMovement(Vector3 position)
        {
            if (!Entity.CanMove())
                return;
            if (!Entity.IsOwnerClientOrOwnedByServer)
                return;
            SetMovePaths(position);
        }

        public void SetExtraMovementState(ExtraMovementState extraMovementState)
        {
            if (!Entity.CanMove())
                return;
            if (!Entity.IsOwnerClientOrOwnedByServer)
                return;
            _tempExtraMovementState = extraMovementState;
        }

        public void SetLookRotation(Quaternion rotation, bool immediately)
        {
            if (!Entity.CanMove() || !Entity.CanTurn())
                return;
            if (!Entity.IsOwnerClientOrOwnedByServer)
                return;
            _targetYAngle = rotation.eulerAngles.y;
            _lookRotationApplied = false;
            if (immediately)
                TurnImmediately(_targetYAngle);
        }

        public Quaternion GetLookRotation()
        {
            return Quaternion.Euler(0f, EntityTransform.eulerAngles.y, 0f);
        }

        public void StopMove()
        {
            StopMoveFunction();
        }

        protected void StopMoveFunction()
        {
            _inputDirection = Vector3.zero;
            CacheAgent.Stop();
        }

        public async void Teleport(Vector3 position, Quaternion rotation, bool stillMoveAfterTeleport)
        {
            if (!IsServer)
            {
                Logging.LogWarning(nameof(NavMeshEntityMovement), $"Teleport function shouldn't be called at client {name}");
                return;
            }
            if (_serverTeleportState != MovementTeleportState.None)
            {
                // Still waiting for teleport responding
                return;
            }
            await OnTeleport(position, rotation, stillMoveAfterTeleport);
        }

        protected async UniTask OnTeleport(Vector3 position, Quaternion rotation, bool stillMoveAfterTeleport)
        {
            if (Vector3.Distance(position, EntityTransform.position) <= MIN_DISTANCE_TO_TELEPORT)
            {
                // Too close to teleport
                return;
            }
            // Prepare before move
            if (IsServer && !IsOwnerClientOrOwnedByServer)
            {
                _serverTeleportState = MovementTeleportState.WaitingForResponse;
            }
            if (TeleportPreparer != null)
            {
                await TeleportPreparer.PrepareToTeleport(position, rotation);
            }
            // Move character to target position
            Vector3 beforeWarpDest = CacheAgent.EntityBody.Destination;
            transform.position = position;
            if (!stillMoveAfterTeleport)
                CacheAgent.Stop();
            if (stillMoveAfterTeleport)
                CacheAgent.SetDestinationDeferred(beforeWarpDest);
            TurnImmediately(rotation.eulerAngles.y);
            // Prepare teleporation states
            if (IsServer && !IsOwnerClientOrOwnedByServer)
            {
                _serverTeleportState = MovementTeleportState.Requesting;
                if (stillMoveAfterTeleport)
                    _serverTeleportState |= MovementTeleportState.StillMoveAfterTeleport;
                _serverTeleportState |= MovementTeleportState.WaitingForResponse;
            }
            if (!IsServer && IsOwnerClient)
            {
                _clientTeleportState = MovementTeleportState.Responding;
            }
        }

        public async UniTask WaitClientTeleportConfirm()
        {
            while (this != null && _serverTeleportState != MovementTeleportState.None)
            {
                await UniTask.Delay(100);
            }
        }

        public bool IsWaitingClientTeleportConfirm()
        {
            return _serverTeleportState != MovementTeleportState.None;
        }

        protected float GetPathRemainingDistance()
        {
            if (!CacheAgentNavMesh.HasEntityPath)
                return -1f;

            float distance = 0.0f;
            // NOTE: Have only one path?
            ProjectDawn.Navigation.NavMeshPath path = CacheAgentNavMesh.EntityPath;
            distance += Vector3.Distance(path.Location.position, path.EndLocation.position);

            return distance;
        }

        public override void EntityUpdate()
        {
            if (!CanSimulateMovement())
                return;
            using (s_UpdateProfilerMarker.Auto())
            {
                float deltaTime = Time.deltaTime;
                UpdateMovement(deltaTime);
                UpdateRotation(deltaTime);
                _isDashing = false;
            }
        }

        public void UpdateMovement(float deltaTime)
        {
            if (IsPreparingToTeleport)
                return;

            ApplyMovementForceMode replaceMovementForceApplierMode = ApplyMovementForceMode.Default;
            // Update force applying
            // Dashing
            if (_isDashing)
            {
                // Can have only one replace movement force applier, so remove stored ones
                _movementForceAppliers.RemoveReplaceMovementForces();
                _movementForceAppliers.Add(new EntityMovementForceApplier().Apply(
                    ApplyMovementForceMode.Dash, EntityTransform.forward, ApplyMovementForceSourceType.None, 0, 0, dashingForceApplier));
            }

            // Apply Forces
            _forceUpdateListeners.OnPreUpdateForces(_movementForceAppliers);
            _movementForceAppliers.UpdateForces(deltaTime,
                Entity.GetMoveSpeed(MovementState.Forward, ExtraMovementState.None),
                out Vector3 forceMotion, out EntityMovementForceApplier replaceMovementForceApplier);
            _forceUpdateListeners.OnPostUpdateForces(_movementForceAppliers);

            // Replace player's movement by this
            if (replaceMovementForceApplier != null)
            {
                // Still dashing to add dash to movement state
                replaceMovementForceApplierMode = replaceMovementForceApplier.Mode;
                // Force turn to dashed direction
                _targetYAngle = Quaternion.LookRotation(replaceMovementForceApplier.Direction).eulerAngles.y;
                // Change move speed to dash force
                if (CacheAgentNavMesh.HasEntityPath)
                    CacheAgent.Stop();
                EntityTransform.position += replaceMovementForceApplier.CurrentSpeed * replaceMovementForceApplier.Direction * deltaTime;
            }

            if (forceMotion.magnitude > MIN_DIRECTION_SQR_MAGNITUDE)
            {
                EntityTransform.position += forceMotion * deltaTime;
            }

            MovementState = MovementState.IsGrounded;

            if (replaceMovementForceApplierMode == ApplyMovementForceMode.Dash)
                MovementState |= MovementState.IsDash;

            if (!replaceMovementForceApplierMode.IsReplaceMovement())
            {
                if (_inputDirection.sqrMagnitude > MIN_DIRECTION_SQR_MAGNITUDE)
                {
                    // Moving by WASD keys
                    MovementState |= MovementState.Forward;
                    ExtraMovementState = this.ValidateExtraMovementState(MovementState, _tempExtraMovementState);
                    if (CacheAgentNavMesh.HasEntityPath)
                        CacheAgent.Stop();
                    float speed = Entity.GetMoveSpeed(MovementState, ExtraMovementState);
                    EntityTransform.position += _inputDirection * speed * deltaTime;
                    // Turn character to destination
                    if (_lookRotationApplied && Entity.CanTurn())
                        _targetYAngle = Quaternion.LookRotation(_inputDirection).eulerAngles.y;
                }
                else
                {
                    // Moving by clicked position
                    bool isMoving = math.length(CacheAgent.EntityBody.Velocity) > MIN_MAGNITUDE_TO_DETERMINE_MOVING;
                    MovementState |= isMoving ? MovementState.Forward : MovementState.None;
                    ExtraMovementState = this.ValidateExtraMovementState(MovementState, _tempExtraMovementState);
                    float speed = Entity.GetMoveSpeed(MovementState, ExtraMovementState);
                    SetAgentMoveSpeed(speed);
                    // Turn character to destination
                    if (isMoving && _lookRotationApplied && Entity.CanTurn())
                        _targetYAngle = Quaternion.LookRotation(math.normalize(CacheAgent.EntityBody.Velocity)).eulerAngles.y;
                }
            }
        }

        public void SetAgentMoveSpeed(float speed)
        {
            if (Mathf.Approximately(CacheAgent.EntityBody.Speed, speed))
                return;
            AgentBody entityBody = CacheAgent.EntityBody;
            if (math.lengthsq(entityBody.Velocity) <= MIN_DIRECTION_SQR_MAGNITUDE)
                return;
            entityBody.Velocity = math.normalize(entityBody.Velocity) * speed;
            CacheAgent.EntityBody = entityBody;
        }

        public void UpdateRotation(float deltaTime)
        {
            if (_yTurnSpeed <= 0f)
                _yAngle = _targetYAngle;
            else if (Mathf.Abs(_yAngle - _targetYAngle) > 1f)
                _yAngle = Mathf.LerpAngle(_yAngle, _targetYAngle, _yTurnSpeed * deltaTime);
            _lookRotationApplied = true;
            RotateY();
        }

        protected void RotateY()
        {
            EntityTransform.eulerAngles = new Vector3(0f, _yAngle, 0f);
        }

        public bool WriteClientState(uint writeTick, NetDataWriter writer, out bool shouldSendReliably)
        {
            if (_clientTeleportState.Has(MovementTeleportState.Responding))
            {
                shouldSendReliably = true;
                writer.Put((byte)_clientTeleportState);
                _clientTeleportState = MovementTeleportState.None;
                return true;
            }
            shouldSendReliably = false;
            return false;
        }

        public bool WriteServerState(uint writeTick, NetDataWriter writer, out bool shouldSendReliably)
        {
            if (_serverTeleportState.Has(MovementTeleportState.Requesting))
            {
                shouldSendReliably = true;
                writer.Put((byte)_serverTeleportState);
                writer.Put(EntityTransform.position.x);
                writer.Put(EntityTransform.position.y);
                writer.Put(EntityTransform.position.z);
                writer.PutPackedUShort(Mathf.FloatToHalf(EntityTransform.eulerAngles.y));
                _serverTeleportState = MovementTeleportState.WaitingForResponse;
                return true;
            }
            shouldSendReliably = false;
            return false;
        }

        public void ReadClientStateAtServer(uint peerTick, NetDataReader reader)
        {
            MovementTeleportState movementTeleportState = (MovementTeleportState)reader.GetByte();
            if (movementTeleportState.Has(MovementTeleportState.Responding))
            {
                _serverTeleportState = MovementTeleportState.None;
                return;
            }
        }

        public async void ReadServerStateAtClient(uint peerTick, NetDataReader reader)
        {
            MovementTeleportState movementTeleportState = (MovementTeleportState)reader.GetByte();
            if (movementTeleportState.Has(MovementTeleportState.Requesting))
            {
                Vector3 position = new Vector3(
                    reader.GetFloat(),
                    reader.GetFloat(),
                    reader.GetFloat());
                float rotation = Mathf.HalfToFloat(reader.GetPackedUShort());
                bool stillMoveAfterTeleport = movementTeleportState.Has(MovementTeleportState.StillMoveAfterTeleport);
                await OnTeleport(position, Quaternion.Euler(0f, rotation, 0f), stillMoveAfterTeleport);
                return;
            }
        }

        public void TurnImmediately(float yAngle)
        {
            _yAngle = _targetYAngle = yAngle;
            RotateY();
        }

        public bool AllowToJump()
        {
            return false;
        }

        public bool AllowToDash()
        {
            return true;
        }

        public bool AllowToCrouch()
        {
            return true;
        }

        public bool AllowToCrawl()
        {
            return true;
        }
    }
}
