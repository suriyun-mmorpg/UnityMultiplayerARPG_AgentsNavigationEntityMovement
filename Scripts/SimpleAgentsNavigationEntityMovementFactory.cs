using ProjectDawn.Navigation.Hybrid;
using UnityEngine;
using UnityEngine.AI;

namespace MultiplayerARPG
{
    public class SimpleAgentsNavigationEntityMovementFactory : IEntityMovementFactory
    {
        public string Name => "Simple Nav Mesh Entity Movement";

        public DimensionType DimensionType => DimensionType.Dimension3D;

        public SimpleAgentsNavigationEntityMovementFactory()
        {

        }

        public bool ValidateSourceObject(GameObject obj)
        {
            return true;
        }

        public IEntityMovementComponent Setup(GameObject obj, ref Bounds bounds)
        {
            bounds = default;
            MeshRenderer[] meshes = obj.GetComponentsInChildren<MeshRenderer>();
            for (int i = 0; i < meshes.Length; ++i)
            {
                if (i > 0)
                    bounds.Encapsulate(meshes[i].bounds);
                else
                    bounds = meshes[i].bounds;
            }

            SkinnedMeshRenderer[] skinnedMeshes = obj.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skinnedMeshes.Length; ++i)
            {
                if (i > 0)
                    bounds.Encapsulate(skinnedMeshes[i].bounds);
                else
                    bounds = skinnedMeshes[i].bounds;
            }

            float scale = Mathf.Max(obj.transform.localScale.x, obj.transform.localScale.y, obj.transform.localScale.z);
            bounds.size = bounds.size / scale;
            bounds.center = bounds.center / scale;

            CapsuleCollider capsuleCollider = obj.AddComponent<CapsuleCollider>();
            capsuleCollider.height = bounds.size.y;
            capsuleCollider.radius = Mathf.Min(bounds.extents.x, bounds.extents.z);
            capsuleCollider.center = Vector3.zero + (Vector3.up * capsuleCollider.height * 0.5f);
            capsuleCollider.isTrigger = true;

            // NOTE: implement this later
            /*
            AgentCylinderShapeAuthoring navMeshAgent = obj.AddComponent<AgentCylinderShapeAuthoring>();
            navMeshAgent.Height = bounds.size.y;
            navMeshAgent.Radius = Mathf.Min(bounds.extents.x, bounds.extents.z);
            */

            return obj.AddComponent<SimpleAgentsNavigationEntityMovement>();
        }
    }
}
