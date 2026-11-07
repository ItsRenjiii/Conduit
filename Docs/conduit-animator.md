# Animator, mask & clip authoring

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before authoring an AnimatorController, AvatarMask, or AnimationClip.**
>
> **This file owns AUTHORING these assets, and nothing else goes in it.** Driving an Animator from a
> behaviour at runtime belongs to `conduit-udonsharp.md`; moving a value over time without an
> AnimationClip belongs to `conduit-vrctween.md`, which is usually the better tool; what a verb's payload
> looks like is published with the verb. A fact discovered while authoring a controller still belongs to
> whichever subject owns it.

**Conduit authors AnimatorControllers, AvatarMasks and AnimationClips natively - never write a throwaway
editor script for animator work.** The verbs and their exact payloads come from
`unity-tool-list animator animation-clip`; what follows is only what the signatures cannot tell you.

- **`animator-get` is the ONLY full-graph read-back.** `assets-get-data` cannot traverse this
  multi-object asset, so it is not an alternative and not a cheaper first look.
- **A parameter's TYPE cannot be changed in place, and that refusal is protecting you.** Every condition
  and blend tree referencing it would keep compiling against the old type and silently stop firing, so a
  type change is a remove and re-add with every referencing condition re-checked.
- **`Equals` against a Float never fires, so it is refused rather than written.** Float equality on a
  value Unity interpolates is a condition that can be correct in the inspector and never true at runtime.
- **`BlendTree.useAutomaticThresholds` defaults ON, and Unity RECOMPUTES thresholds evenly the moment
  `children` is assigned while it is on.** So the flag has to be set BEFORE assigning children, or
  authored thresholds are silently replaced with an even spread. Supplying explicit child thresholds
  defaults it off.
- **`controller.layers` and `controller.parameters` return COPIES.** Mutating an array element does
  nothing; the whole array goes back through the setter or the edit is a no-op.
- **A StateMachineBehaviour cannot carry world logic - it is plain C# and is stripped from the upload**
  (`conduit-udonsharp.md`), yet it runs in play mode, so a state entry that worked through every test
  does nothing in the client and nothing reports it. Drive the controller from a U# behaviour instead.
- **World NPCs are humanoid Animators like any avatar rig**, so masks, IK and additive layers are fully in
  scope for a world project rather than avatar-only features.
