using Content.Server.Actions;
using Content.Shared.Popups;
using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Interaction;
using Content.Server.DoAfter;
using Content.Server.GameTicking;
using Content.Shared.Stunnable;
using Content.Shared.Revenant;
using Robust.Server.GameObjects;
using Robust.Shared.Random;
using Content.Shared.StatusEffect;
using Content.Server.Visible;
using Content.Shared.Examine;
using Robust.Shared.Prototypes;
using Content.Shared.Actions.ActionTypes;
using Content.Shared.Tag;
using Content.Server.Store.Components;
using Content.Server.Store.Systems;
using Content.Shared.FixedPoint;
using Robust.Shared.Player;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Revenant.Components;

namespace Content.Server.Revenant.EntitySystems;

public sealed partial class RevenantSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ActionsSystem _action = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PhysicsSystem _physics = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedInteractionSystem _interact = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly StoreSystem _store = default!;
    [Dependency] private readonly VisibilitySystem _visibility = default!;
    [Dependency] private readonly GameTicker _ticker = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RevenantComponent, ComponentStartup>(OnStartup);

        SubscribeLocalEvent<RevenantComponent, RevenantShopActionEvent>(OnShop);
        SubscribeLocalEvent<RevenantComponent, DamageChangedEvent>(OnDamage);
        SubscribeLocalEvent<RevenantComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<RevenantComponent, StatusEffectAddedEvent>(OnStatusAdded);
        SubscribeLocalEvent<RevenantComponent, StatusEffectEndedEvent>(OnStatusEnded);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(_ => MakeVisible(true));

        InitializeAbilities();
    }

    private void OnStartup(EntityUid uid, RevenantComponent component, ComponentStartup args)
    {
        //update the icon
        ChangeEssenceAmount(uid, 0, component);

        //default the visuals
        _appearance.SetData(uid, RevenantVisuals.Corporeal, false);
        _appearance.SetData(uid, RevenantVisuals.Harvesting, false);
        _appearance.SetData(uid, RevenantVisuals.Stunned, false);

        if (_ticker.RunLevel == GameRunLevel.PostRound && TryComp<VisibilityComponent>(uid, out var visibility))
        {
            _visibility.AddLayer(visibility, (int) VisibilityFlags.Ghost, false);
            _visibility.RemoveLayer(visibility, (int) VisibilityFlags.Normal, false);
            _visibility.RefreshVisibility(visibility);
        }

        //ghost vision
        if (TryComp(component.Owner, out EyeComponent? eye))
            eye.VisibilityMask |= (uint) (VisibilityFlags.Ghost);

        var shopaction = new InstantAction(_proto.Index<InstantActionPrototype>("RevenantShop"));
        _action.AddAction(uid, shopaction, null);
    }

    private void OnStatusAdded(EntityUid uid, RevenantComponent component, StatusEffectAddedEvent args)
    {
        if (args.Key == "Stun")
            _appearance.SetData(uid, RevenantVisuals.Stunned, true);
    }

    private void OnStatusEnded(EntityUid uid, RevenantComponent component, StatusEffectEndedEvent args)
    {
        if (args.Key == "Stun")
            _appearance.SetData(uid, RevenantVisuals.Stunned, false);
    }

    private void OnExamine(EntityUid uid, RevenantComponent component, ExaminedEvent args)
    {
        if (args.Examiner == args.Examined)
        {
            args.PushMarkup(Loc.GetString("revenant-essence-amount",
                ("current", component.Essence.Int()), ("max", component.EssenceRegenCap.Int())));
        }
    }

    private void OnDamage(EntityUid uid, RevenantComponent component, DamageChangedEvent args)
    {
        if (!HasComp<CorporealComponent>(uid) || args.DamageDelta == null)
            return;

        var essenceDamage = args.DamageDelta.Total.Float() * component.DamageToEssenceCoefficient * -1;
        ChangeEssenceAmount(uid, essenceDamage, component);
    }

    public bool ChangeEssenceAmount(EntityUid uid, FixedPoint2 amount, RevenantComponent? component = null, bool allowDeath = true, bool regenCap = false)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (!allowDeath && component.Essence + amount <= 0)
            return false;

        component.Essence += amount;

        if (regenCap)
            FixedPoint2.Min(component.Essence, component.EssenceRegenCap);

        if (TryComp<StoreComponent>(uid, out var store))
            _store.UpdateUserInterface(uid, store);

        _alerts.ShowAlert(uid, AlertType.Essence, (short) Math.Clamp(Math.Round(component.Essence.Float() / 10f), 0, 16));

        if (component.Essence <= 0)
        {
            int amt = _random.Next(1, 3);
            int i = 0;
            while (i < amt)
            {
                Spawn("Ectoplasm", Transform(uid).Coordinates);
                i++;
            }
            QueueDel(uid);
        }
        return true;
    }

    private bool TryUseAbility(EntityUid uid, RevenantComponent component, FixedPoint2 abilityCost, Vector2 debuffs)
    {
        if (component.Essence <= abilityCost)
        {
            _popup.PopupEntity(Loc.GetString("revenant-not-enough-essence"), uid, uid);
            return false;
        }

        var tileref = Transform(uid).Coordinates.GetTileRef();
        if (tileref != null)
        {
            if(_physics.GetEntitiesIntersectingBody(uid, (int) CollisionGroup.Impassable).Count > 0)
            {
                _popup.PopupEntity(Loc.GetString("revenant-in-solid"), uid, uid);
                return false;
            }
        }

        ChangeEssenceAmount(uid, abilityCost, component, false);

        _statusEffects.TryAddStatusEffect<CorporealComponent>(uid, "Corporeal", TimeSpan.FromSeconds(debuffs.Y), false);
        _stun.TryStun(uid, TimeSpan.FromSeconds(debuffs.X), false);

        return true;
    }

    private void OnShop(EntityUid uid, RevenantComponent component, RevenantShopActionEvent args)
    {
        if (!TryComp<StoreComponent>(uid, out var store))
            return;
        _store.ToggleUi(uid, store);
    }

    public void MakeVisible(bool visible)
    {
        foreach (var (_, vis) in EntityQuery<RevenantComponent, VisibilityComponent>())
        {
            if (visible)
            {
                _visibility.AddLayer(vis, (int) VisibilityFlags.Normal, false);
                _visibility.RemoveLayer(vis, (int) VisibilityFlags.Ghost, false);
            }
            else
            {
                _visibility.AddLayer(vis, (int) VisibilityFlags.Ghost, false);
                _visibility.RemoveLayer(vis, (int) VisibilityFlags.Normal, false);
            }
            _visibility.RefreshVisibility(vis);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var rev in EntityQuery<RevenantComponent>())
        {
            rev.Accumulator += frameTime;

            if (rev.Accumulator <= 1)
                continue;
            rev.Accumulator -= 1;

            if (rev.Essence < rev.EssenceRegenCap)
            {
                ChangeEssenceAmount(rev.Owner, rev.EssencePerSecond, rev, regenCap: true);
            }
        }
    }
}
