# NavMesh, agents and physics queries in Udon

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before concluding that a NavMesh, agent or physics-query API is unavailable to
> Udon, and before writing anything that has to path, ground itself or probe the world.**
>
> **This file owns one question - WHAT IS EXPOSED for pathing and probing - and nothing else goes in it.**
> It is a list, kept so nobody re-derives it. The COST of running a query every frame is
> `conduit-performance.md`; the SIZE and PLACEMENT of the colliders a query hits are
> `conduit-scene-geometry.md`; a Dynamics contact is `conduit-dynamics.md` and is not a physics query at
> all. How to interrogate the registry yourself, for any subject, is `conduit-diagnostics.md`.

## The whitelist is far larger than it looks

**Never design around a missing NavMesh or physics API. They are exposed, and have been for years.**
The reflex to assume Udon cannot pathfind produces a naive hand-rolled reimplementation of something the
platform already does properly. The lists below are the ANSWER, recorded so nobody re-derives them.

Derived from `VRC.Udon.Wrapper.dll` in the worlds package, SDK 3.10.4.

## AI and NavMesh types exposed

All 28 present, each with an array form:

`NavMesh`, `NavMeshAgent`, `NavMeshObstacle`, `NavMeshData`, `NavMeshDataInstance`, `NavMeshLinkData`,
`NavMeshLinkInstance`, `NavMeshPath`, `NavMeshHit`, `NavMeshQueryFilter`, `NavMeshBuildSettings`,
`NavMeshTriangulation`, `NavMeshCollectGeometry`, `OffMeshLink`, `OffMeshLinkData`.

- **Runtime navmesh data is included**, not just the agent: `AddNavMeshData`, `RemoveNavMeshData` and
  `RemoveAllNavMeshData` mean surfaces can be added and removed while the world is running.
- **`NavMeshQueryFilter` and `NavMeshBuildSettings` are exposed**, so area costs and per-query filtering
  are available rather than just the default agent behaviour.
- **`NavMeshTriangulation` and `NavMeshCollectGeometry` are exposed**, which is raw navmesh geometry.

## Physics query families exposed

Every family, each in plain, `All` and `NonAlloc` forms:

`Raycast`, `SphereCast`, `BoxCast`, `CapsuleCast`, `Linecast`, `OverlapSphere`, `OverlapBox`,
`OverlapCapsule`, `CheckSphere`, `CheckBox`, `CheckCapsule`, `ComputePenetration`, `ClosestPoint`,
`ClosestPointOnBounds`, `ClosestPointOnPlane`. 56 distinct overloads in total.

- **Prefer the `NonAlloc` forms in anything that runs per frame**, for the same reason as any Unity
  project: the allocating forms produce garbage the collector then has to chase.
- **`CapsuleCast` is available**, so a volumetric fit test is a real option and does not need faking with
  a fan of rays.

## Confirming a member, and re-checking after an SDK update

- **The extern list proves a member is REACHABLE; the compiler proves your OVERLOAD is.** Both beat a
  memory of what Udon cannot do. Compile the call rather than reasoning about it.
- **When the SDK version moves, re-derive rather than trusting the lists above.** The mangled extern
  names live in `Runtime/Udon/External/VRC.Udon.Wrapper.dll` under the worlds package; searching it for a
  type or method name settles exposure in seconds, and the version stamp above says which SDK these lists
  were last derived against.
- **VRChat ships a working reference scene** at `Samples/AINavmeshScene/` in the worlds package.
