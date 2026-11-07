# Creator Economy (VRC.Economy)

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before touching products, listings, purchases, ownership, or a supporter board.**
>
> **This file owns `VRC.Economy` and nothing else goes in it.** **"OWNERSHIP" names two unrelated things
> in this project and that is the trap here:** owning a PRODUCT is this file, owning a network OBJECT is
> `conduit-networking.md`, and a fact about one is never a fact about the other. Drawing a supporter board
> on a canvas is `conduit-vrchat-ui.md`; what ClientSim simulates or fakes anywhere else is
> `conduit-testing.md`, with only the economy-specific mocks recorded here.

## What is actually exposed to Udon

`VRC.Economy` is Udon-exposed, but **not all of it - presence in `VRCEconomy.dll` is NOT evidence a member
is callable.** Verified by compiling each one (SDK 3.10.4):

- **Not exposed:** `Store.OpenProduct(IProduct)` fails with "Method is not exposed to Udon". An explicit
  `(IProduct)` cast does not help; the member is absent from the whitelist.
- **Exposed and confirmed compiling:** `OpenListing(string)`, `OpenWorldStorePage()`, `OpenStore()`,
  `OpenGroupListing(string)`, `OpenMarketplaceStore(string,string,string)`,
  `ListPurchases(IUdonEventReceiver, VRCPlayerApi)`, `DoesPlayerOwnProduct`, `GetPlayersWhoOwnProduct`,
  `ListProductOwners(IUdonEventReceiver, IProduct)`.

So a world reaches one specific purchase screen by **listing id, not by the product asset**. A serialized
`VRC.Economy.UdonProduct` field DOES compile in an UdonSharpBehaviour (`using VRC.Economy;`) and is the
right way to hold the product for ownership checks.

## Ownership callbacks

**Ownership is not available at `Start`.** Drive it from the `OnPurchasesLoaded` /
`OnPurchaseConfirmed(Multiple)` / `OnPurchaseExpired` overrides.

**Implementing both confirmation forms is safe ONLY when the handler is idempotent** (sets a boolean,
counts nothing). A handler that counts or grants per event would double-apply. Observed in the live client
on a real purchase: BOTH forms fire - 3-arg `OnPurchaseConfirmed` FIRST, then `OnPurchaseConfirmedMultiple`
an instant later - one purchase, two confirm events.

Take both idempotently: ClientSim raises only the 3-arg `_onPurchaseConfirmed`
(`ClientSimStoreManager.PurchaseConfirmed`), so a Multiple-only script works live but stays dead to
ClientSim's mid-play mock purchases.

## Supporter boards: `Store.ListProductOwners`

**This is how a world lists EVERY owner of a product, not just the ones in the instance.** Signature
`Store.ListProductOwners(IUdonEventReceiver, IProduct)`, answered asynchronously on
`public override void OnListProductOwners(IProduct product, string[] owners)`.

Different axis from the other ownership members: `DoesPlayerOwnProduct` / `GetPlayersWhoOwnProduct` answer
about players PRESENT in the instance, while `ListProductOwners` returns owner display names globally - so
a board can credit supporters who are not online. Confirmed working end to end in ClientSim play mode.

1. **The product must have "Enable Access to Owner Names" turned on** in its VRChat listing settings, or
   the names are not available to the world.
2. **An EMPTY `owners` array is the correct, expected result for a product nobody has bought yet, and it
   is NOT evidence the call failed.** During development the happy path and the broken path look nearly
   identical. **Do not "fix" an empty board. The discriminator is whether the CALLBACK FIRES, not what the
   array contains**: log `owners.Length` and the product from inside `OnListProductOwners` behind the debug
   gate. A callback that fires with 0 names means the API works and nobody qualifies; a callback that never
   fires is the real fault. **A creator who buys their OWN product DOES appear in `ListProductOwners`** -
   observed live across three shipped projects - so a self-purchase is a valid way to prove a board end to
   end against real data instead of waiting on a customer.
3. **ClientSim raises `OnListProductOwners` TWICE for a single request**, the second arriving after the
   handler has already completed its round - the same double-raise shape as the purchase callbacks. A
   handler that counts, appends, or grants per callback WILL double-apply; guard with a round/gathering
   flag that makes the late arrival a no-op.
4. The reply is a callback, so a display gathering from N products needs a completion counter; the
   callback carries the `IProduct` so replies can be attributed. Names arrive as raw display-name strings -
   dedupe, sorting, and empty-state text are the consuming world's job.
5. ClientSim supplies mock owner names, so the CE path can be exercised without any real purchase. That
   makes ClientSim, not the live client, the cheapest place to prove a supporter display works.

## Authoring the UdonProduct asset

**The asset is NOT created from the manager window.** `UdonProduct` carries
`[CreateAssetMenu(menuName = "VRChat/UdonProduct")]`, so it comes from right-click in the Project window:
Create > VRChat > UdonProduct. Its fields are **API ID Reference** (tooltip "ID to match in API"), Name,
Description, Image - you paste the product's id into the first and the other three are fetched for you.

**The window is VRChat SDK > Utilities > UdonProducts Manager, and running it is not optional.** It offers
"Import/Refresh all UdonProducts" (default folder `Assets/UdonProducts`), groups them as "UdonProducts in
the project" / "UdonProducts used in the scene", and gives a **Copy Listing ID** button per listing. It is
the only thing that writes the scene descriptor's `udonProducts` list (hence its "Unable to find scene
descriptor in scene" error), and the world builder uploads that list as `world.UdonProducts` - so skipping
the window ships a world with no products attached. It rejects anything that is not a product: "Other
types of products, such as listings or group roles, are not supported."

## The listing id is not the product id, and the editor cannot catch the mix-up

`Store.OpenListing` takes a **listing** id. Pasting the product's own `prod_...` id makes the buy button
open NOTHING in the client, with no error the player sees.

- **The Copy Listing ID button exists ONLY in the Sort by Listings view**, on each listing's header row
  (`ListingInfo.uxml`). The default Scene/Project view has no such button anywhere, so telling a user to
  look "beside the product" sends them hunting for a control that is not there.
- Listings only populate when the SDK is logged in and the account has listings. The listing page URL on
  vrchat.com (Marketplace > Storefront) carries the same id.
- **Do NOT judge the id by its prefix.** Real LISTING ids also read `prod_<uuid>` - a working drawer opened
  from `OpenListing("prod_<uuid>")` and the client hit `GET listing/<that id>` - so a prefix check
  would false-flag a correct value. **The reliable validator is equality: the field matching the
  UdonProduct asset's own `_ID` is always wrong.**
- **Failure signature of a bad id:** an `[API] Requesting Get listing/<id>` line at press time followed by
  no purchase drawer. Grep the client log for `Get listing/` to read back exactly what the button passed.
- Raise a Start-time config error when the two ids are equal (ungated, like other Start config errors) and
  trace the buy chain through gated debug logging. Tick Enable Debug Logging before an upload meant for a
  store test - the client log is the only place the store flow can be observed.
