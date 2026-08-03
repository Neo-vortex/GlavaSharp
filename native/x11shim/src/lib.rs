//! x11shim (Rust) — the X11-side half of GlavaSharp's `--desktop` mode.
//! Same shape as native/pwshim: a tiny flat C ABI (start/stop), the actual
//! protocol work done in Rust instead of hand-rolled Xlib P/Invoke from C#.
//!
//! GLFW (via OpenTK on the C# side) owns window/context creation; this crate
//! only takes the resulting X11 window ID and, on its own connection to the
//! X server, does the EWMH work GLFW has no concept of:
//!
//!   - marks the window `_NET_WM_WINDOW_TYPE_NORMAL` (deliberately *not*
//!     `_DESKTOP` -- see "Why NORMAL+BELOW, not DESKTOP" below)
//!   - adds `_NET_WM_STATE_BELOW` + `_NET_WM_STATE_STICKY` +
//!     `_NET_WM_STATE_SKIP_TASKBAR` + `_NET_WM_STATE_SKIP_PAGER` ("pinned"/
//!     "below" are the same two states GLava's own `env_Xfwm4.glsl`
//!     requests; the two SKIP states replace what `_NET_WM_WINDOW_TYPE_DESKTOP`
//!     used to imply for free)
//!   - strips decorations via `_MOTIF_WM_HINTS`
//!   - positions/sizes the window -- by default the whole screen, or a
//!     specific rect when the caller passes one (GLava's `setgeometry`
//!     equivalent for desktop mode, see `--desktop-geometry` in Program.cs)
//!   - gives the window an empty SHAPE-extension input region, making it
//!     fully click-through unconditionally, so it never intercepts clicks
//!     meant for desktop icons regardless of stacking order
//!   - as a defensive fallback (not the primary mechanism -- see below),
//!     restacks itself *above* whichever xfdesktop window(s) overlap its own
//!     geometry, re-asserting on every `_NET_CLIENT_LIST_STACKING` change
//!     and on a periodic timer
//!
//! ## Why NORMAL+BELOW, not DESKTOP
//!
//! Earlier versions marked this window `_NET_WM_WINDOW_TYPE_DESKTOP` too,
//! reasoning that mirroring xfdesktop's own type would let plain stacking
//! requests (first `BELOW` xfdesktop, then, after that was found to hide
//! the window entirely, `ABOVE` xfdesktop) place us correctly relative to
//! it. Both failed, and for the same underlying reason: this window is
//! WM-managed (xfwm4 reparented it), so any `ConfigureWindow` restack we
//! issue on it isn't applied directly by the X server -- root has
//! `SubstructureRedirect` set, so it's redirected to xfwm4 as a
//! `ConfigureRequest` *event*, and xfwm4 decides whether to actually honor
//! it. `.check()` only surfaces X protocol errors, not "the WM silently
//! declined it," so a declined request looks identical to a successful one
//! from our side. Per the EWMH spec, `_NET_WM_WINDOW_TYPE_DESKTOP` windows
//! are meant to always sit beneath *every other window type*, as a
//! WM-enforced invariant -- and xfwm4 enforces it hard: `xprop` on this
//! window while it still requested `_DESKTOP` showed `_NET_WM_STATE` as
//! `STICKY, SKIP_PAGER, SKIP_TASKBAR` -- xfwm4 was silently overwriting our
//! requested state (including `BELOW`) with its own computed set, i.e. it
//! was never honoring our state request at all for a `_DESKTOP`-typed
//! window. By also claiming `_DESKTOP`, we put ourselves in the same
//! bottom-of-everything bucket as xfdesktop itself, where xfwm4
//! tie-breaks in xfdesktop's favor on every stacking recalculation --
//! exactly what a desktop click triggers (`click_to_focus` +
//! `raise_on_click` re-raise xfdesktop), permanently sinking us with no
//! error and nothing to catch.
//!
//! The fix: don't claim `_DESKTOP` at all. `_NET_WM_WINDOW_TYPE_NORMAL` +
//! `_NET_WM_STATE_BELOW` puts this window in the ordinary "below normal
//! windows" layer, which every EWMH-compliant WM (xfwm4 included) keeps
//! *above* the desktop layer as a structural invariant, not a per-window
//! tie-break -- so there's no fight to lose. `SKIP_TASKBAR`/`SKIP_PAGER`
//! are requested explicitly since `_DESKTOP` no longer implies them.
//! (`xwinwrap`, the standard X11 "run something as the desktop background"
//! tool, sidesteps this whole class of problem differently -- via
//! override-redirect, which exempts a window from WM management/redirect
//! entirely. That's not done here since it would need creating the X11
//! window ourselves instead of letting GLFW own creation.)
//!
//! The restack-above-xfdesktop logic (originally the primary mechanism) is
//! kept as a defensive fallback: it's a manual `ConfigureWindow`, still
//! subject to the same WM redirection described above, so it's only ever
//! as reliable as xfwm4 choosing to honor it -- but now that we're not
//! fighting a hard-enforced type invariant, there's no known reason for it
//! to be declined, and it costs little to leave running as a second line of
//! defense (e.g. if some xfwm4 config/version doesn't layer BELOW/DESKTOP
//! the way the spec describes).
//!
//! Uses x11rb instead of linking libX11: it speaks the X11 wire protocol
//! directly over a Unix socket in pure Rust, so unlike pwshim's `pipewire`
//! crate this needs no bindgen/clang/system dev headers at build time.

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use x11rb::atom_manager;
use x11rb::connection::Connection;
use x11rb::protocol::xproto::{
    AtomEnum, ChangeWindowAttributesAux, ClipOrdering, ConfigureWindowAux, ConnectionExt as _,
    EventMask, PropMode, StackMode, Window,
};
use x11rb::protocol::Event;
// change_property32 (a typed convenience wrapper over xproto's raw
// change_property) lives on a separate trait from xproto::ConnectionExt.
use x11rb::wrapper::ConnectionExt as _;
// shape_rectangles (SHAPE extension) -- yet another separate ConnectionExt trait.
use x11rb::protocol::shape::{ConnectionExt as _, SK, SO};

atom_manager! {
    pub AtomCollection: AtomCollectionCookie {
        _NET_WM_WINDOW_TYPE,
        _NET_WM_WINDOW_TYPE_NORMAL,
        _NET_WM_STATE,
        _NET_WM_STATE_BELOW,
        _NET_WM_STATE_STICKY,
        _NET_WM_STATE_SKIP_TASKBAR,
        _NET_WM_STATE_SKIP_PAGER,
        _MOTIF_WM_HINTS,
        _NET_CLIENT_LIST_STACKING,
    }
}

// MWM_HINTS_DECORATIONS bit in the Motif hints `flags` field; a `decorations`
// value of 0 with this bit set means "no decorations at all".
const MWM_HINTS_DECORATIONS: u32 = 1 << 1;

// Minimum gap between re-raise attempts triggered by stacking-change events.
// Without this, our own configure_window(ABOVE) call changes
// _NET_CLIENT_LIST_STACKING, which re-triggers the listener that just fired
// it -- a tight feedback loop. 200ms is imperceptible for "stay above
// xfdesktop" purposes and comfortably breaks that loop.
const RERAISE_MIN_INTERVAL: Duration = Duration::from_millis(200);

// Upper bound on how long we go without re-asserting our stacking position
// even without an observed _NET_CLIENT_LIST_STACKING change. xfwm4 doesn't
// reliably regenerate that property for every internal restack it performs
// (e.g. re-raising xfdesktop in response to a desktop click), so relying on
// PropertyNotify alone can miss the exact moment we get hidden -- this is a
// cheap timer-driven fallback on top of the event-driven path, not a
// replacement for it.
const PERIODIC_RERAISE_INTERVAL: Duration = Duration::from_millis(500);

// (x, y, width, height), all root-relative/absolute -- same convention
// xwininfo's "Absolute upper-left X/Y" + "Width"/"Height" report.
type Rect = (i32, i32, u32, u32);

struct ShimHandle {
    stop: Arc<AtomicBool>,
    join: Option<JoinHandle<()>>,
}

/// # Safety
/// `window_xid` must be a valid X11 window ID for a window on the default
/// display (`$DISPLAY`) that the caller owns and keeps alive for at least as
/// long as this handle is running (i.e. until `x11shim_desktop_mode_stop`).
/// Returns null on failure (connection/setup error, already logged to
/// stderr) -- the caller should treat that as "continue as a normal window."
///
/// `geom_width`/`geom_height` <= 0 means "no override, cover the whole
/// screen" (GlavaSharp's original --desktop behavior); otherwise the window
/// is placed at exactly `(geom_x, geom_y)` sized `geom_width`x`geom_height`,
/// GLava's `setgeometry` equivalent for desktop mode.
#[no_mangle]
pub extern "C" fn x11shim_desktop_mode_start(
    window_xid: u64,
    geom_x: i32,
    geom_y: i32,
    geom_width: i32,
    geom_height: i32,
) -> *mut c_void {
    let geometry = (geom_width > 0 && geom_height > 0).then_some((geom_x, geom_y, geom_width as u32, geom_height as u32));
    match try_start(window_xid as Window, geometry) {
        Ok(handle) => Box::into_raw(Box::new(handle)) as *mut c_void,
        Err(e) => {
            eprintln!("x11shim: desktop mode setup failed: {e}");
            std::ptr::null_mut()
        }
    }
}

#[no_mangle]
pub extern "C" fn x11shim_desktop_mode_stop(ctx: *mut c_void) {
    if ctx.is_null() {
        return;
    }
    let mut handle = unsafe { Box::from_raw(ctx as *mut ShimHandle) };
    handle.stop.store(true, Ordering::Relaxed);
    if let Some(join) = handle.join.take() {
        let _ = join.join();
    }
}

fn try_start(
    window: Window,
    geometry: Option<(i32, i32, u32, u32)>,
) -> Result<ShimHandle, Box<dyn std::error::Error>> {
    let (conn, screen_num) = x11rb::connect(None)?;
    let conn = Arc::new(conn);
    let screen = conn.setup().roots[screen_num].clone();
    let atoms = AtomCollection::new(&*conn)?.reply()?;

    // Re-apply hints while unmapped -- setting _NET_WM_STATE/_NET_WM_WINDOW_TYPE
    // before the (re-)initial map is the EWMH-sanctioned way to make sure a
    // WM honors them, rather than relying on every WM re-reading the property
    // live on an already-mapped, already-managed window. GLFW mapped the
    // window when it created it, so we briefly unmap, set everything, then
    // map again -- invisible to the user since nothing has rendered yet.
    conn.unmap_window(window)?.check()?;

    // Deliberately NOT _NET_WM_WINDOW_TYPE_DESKTOP -- see the module doc
    // comment ("Why NORMAL+BELOW, not DESKTOP") for why that type actively
    // works against us on xfwm4.
    conn.change_property32(
        PropMode::REPLACE,
        window,
        atoms._NET_WM_WINDOW_TYPE,
        AtomEnum::ATOM,
        &[atoms._NET_WM_WINDOW_TYPE_NORMAL],
    )?
    .check()?;

    conn.change_property32(
        PropMode::REPLACE,
        window,
        atoms._NET_WM_STATE,
        AtomEnum::ATOM,
        &[
            atoms._NET_WM_STATE_BELOW,
            atoms._NET_WM_STATE_STICKY,
            atoms._NET_WM_STATE_SKIP_TASKBAR,
            atoms._NET_WM_STATE_SKIP_PAGER,
        ],
    )?
    .check()?;

    conn.change_property32(
        PropMode::REPLACE,
        window,
        atoms._MOTIF_WM_HINTS,
        atoms._MOTIF_WM_HINTS,
        &[MWM_HINTS_DECORATIONS, 0, 0, 0, 0],
    )?
    .check()?;

    let (rect_x, rect_y, rect_w, rect_h) = geometry.unwrap_or((
        0,
        0,
        screen.width_in_pixels as u32,
        screen.height_in_pixels as u32,
    ));
    conn.configure_window(
        window,
        &ConfigureWindowAux::new().x(rect_x).y(rect_y).width(rect_w).height(rect_h),
    )?
    .check()?;

    conn.map_window(window)?.check()?;

    // Make the whole window click-through (empty INPUT shape) via the SHAPE
    // extension, so it never intercepts clicks meant for desktop icons --
    // this is independent of and doesn't depend on stacking order: a
    // visualizer has no reason to receive input at all.
    conn.shape_rectangles(SO::SET, SK::INPUT, ClipOrdering::UNSORTED, window, 0, 0, &[])?
        .check()?;

    // ConfigureWindow's sibling/stack_mode fields require `sibling` to
    // actually be an X11 sibling (same parent) of the window being
    // configured -- since a reparenting WM puts each client's *frame*, not
    // the raw client window, directly under root, both windows have to be
    // walked up to their frame before this is a valid request. (Resolved
    // fresh inside raise_above_desktop_owner on every call, not cached here
    // -- see that function's doc comment for why.)
    let my_rect: Rect = (rect_x, rect_y, rect_w, rect_h);
    raise_above_desktop_owner(&*conn, screen.root, window, &atoms, my_rect)?;

    // Watch the root window for stacking changes so we can re-raise
    // ourselves above xfdesktop if a click (or anything else) restacks it
    // above us.
    conn.change_window_attributes(
        screen.root,
        &ChangeWindowAttributesAux::new().event_mask(EventMask::PROPERTY_CHANGE),
    )?
    .check()?;
    conn.flush()?;

    let stop = Arc::new(AtomicBool::new(false));
    let stop_for_thread = stop.clone();
    let conn_for_thread = conn.clone();
    let stacking_atom = atoms._NET_CLIENT_LIST_STACKING;

    let join = thread::Builder::new()
        .name("glavasharp-x11-desktop".into())
        .spawn(move || {
            run_reraise_loop(
                conn_for_thread,
                screen.root,
                window,
                atoms,
                stacking_atom,
                my_rect,
                stop_for_thread,
            )
        })
        .expect("failed to spawn X11 desktop-mode monitor thread");

    Ok(ShimHandle {
        stop,
        join: Some(join),
    })
}

/// Walks up the parent chain from `win` until it hits a direct child of
/// `root` -- i.e. the frame a reparenting WM wrapped it in, or `win` itself
/// if the WM left it unreparented (common for undecorated/override-redirect
/// windows). That's the window ID that's a valid `sibling` target for
/// restacking relative to another top-level window.
fn toplevel_ancestor<C: Connection>(
    conn: &C,
    root: Window,
    mut win: Window,
) -> Result<Window, Box<dyn std::error::Error>> {
    loop {
        let tree = conn.query_tree(win)?.reply()?;
        if tree.parent == root || tree.parent == 0 {
            return Ok(win);
        }
        win = tree.parent;
    }
}

/// Root-relative geometry of `win` -- absolute position (via
/// `TranslateCoordinates` from `win`'s own origin to `root`) plus size (via
/// `GetGeometry`). Same numbers `xwininfo -id win` reports as "Absolute
/// upper-left X/Y" + "Width"/"Height".
fn absolute_rect<C: Connection>(conn: &C, root: Window, win: Window) -> Option<Rect> {
    let geom = conn.get_geometry(win).ok()?.reply().ok()?;
    let translated = conn
        .translate_coordinates(win, root, 0, 0)
        .ok()?
        .reply()
        .ok()?;
    Some((
        translated.dst_x as i32,
        translated.dst_y as i32,
        geom.width as u32,
        geom.height as u32,
    ))
}

fn rects_overlap(a: Rect, b: Rect) -> bool {
    let (ax, ay, aw, ah) = a;
    let (bx, by, bw, bh) = b;
    ax < bx + bw as i32 && bx < ax + aw as i32 && ay < by + bh as i32 && by < ay + ah as i32
}

/// Finds the topmost mapped window in `_NET_CLIENT_LIST_STACKING` whose
/// `WM_CLASS` mentions "xfdesktop" (case-insensitively covers both the
/// instance and class parts of the property in one string) *and* whose
/// root-relative geometry overlaps `target_rect` -- i.e. the xfdesktop
/// window actually covering the monitor(s) GLava itself was placed on, not
/// just whichever xfdesktop window happens to be topmost overall (which,
/// with one xfdesktop window per monitor, can be the wrong one). Returns
/// its top-level/frame ancestor -- the actual valid `sibling` for a
/// ConfigureWindow restack. Best-effort: any protocol error along the way
/// just means "couldn't find it," not a hard failure.
fn find_desktop_owner_toplevel<C: Connection>(
    conn: &C,
    root: Window,
    me: Window,
    atoms: &AtomCollection,
    target_rect: Rect,
) -> Option<Window> {
    let stacking = conn
        .get_property(false, root, atoms._NET_CLIENT_LIST_STACKING, AtomEnum::WINDOW, 0, u32::MAX)
        .ok()?
        .reply()
        .ok()?;
    let client_ids: Vec<Window> = stacking.value32()?.collect();

    let mut best: Option<Window> = None;
    for &client in &client_ids {
        if client == me {
            continue;
        }
        let Ok(cookie) = conn.get_property(false, client, AtomEnum::WM_CLASS, AtomEnum::STRING, 0, 64) else {
            continue;
        };
        let Ok(prop) = cookie.reply() else { continue };
        if !String::from_utf8_lossy(&prop.value)
            .to_lowercase()
            .contains("xfdesktop")
        {
            continue;
        }
        let Some(rect) = absolute_rect(conn, root, client) else {
            continue;
        };
        if rects_overlap(rect, target_rect) {
            best = Some(client); // last match wins -> topmost overlapping xfdesktop window
        }
    }

    toplevel_ancestor(conn, root, best?).ok()
}

/// Restacks `me`'s toplevel/frame directly above the xfdesktop window(s)
/// covering `target_rect` when we can find one, falling back to a plain
/// sibling-less `Above` restack (top of our own stacking layer) otherwise.
///
/// Resolves `me`'s toplevel via `toplevel_ancestor` fresh on every call
/// rather than accepting a cached one from the caller: xfwm4 doesn't
/// necessarily keep the frame it created at the very first map -- e.g. it's
/// been observed to swap in a new frame shortly after a Motif "no
/// decorations" hint change lands on a remap, which raced ahead of a
/// once-at-startup `toplevel_ancestor` call and left it holding a since-
/// destroyed window ID (`BadWindow` on every subsequent `ConfigureWindow`,
/// forever, since that ID never becomes valid again). Re-resolving here
/// costs one extra `QueryTree` round-trip per call and is immune to that.
fn raise_above_desktop_owner<C: Connection>(
    conn: &C,
    root: Window,
    me: Window,
    atoms: &AtomCollection,
    target_rect: Rect,
) -> Result<(), Box<dyn std::error::Error>> {
    let my_toplevel = toplevel_ancestor(conn, root, me)?;
    let aux = match find_desktop_owner_toplevel(conn, root, me, atoms, target_rect) {
        Some(sibling) if sibling != my_toplevel => {
            ConfigureWindowAux::new().sibling(sibling).stack_mode(StackMode::ABOVE)
        }
        _ => ConfigureWindowAux::new().stack_mode(StackMode::ABOVE),
    };
    conn.configure_window(my_toplevel, &aux)?.check()?;
    Ok(())
}

/// Polls (rather than blocking on `wait_for_event`) so `stop` can be
/// observed promptly without needing a self-pipe/eventfd just to interrupt a
/// blocking socket read -- 50ms poll granularity is unnoticeable for both
/// shutdown latency and "stay above xfdesktop" responsiveness. Re-raises are
/// triggered both by `_NET_CLIENT_LIST_STACKING` changes and, as a fallback,
/// on a plain timer (`PERIODIC_RERAISE_INTERVAL`) in case xfwm4 doesn't
/// regenerate that property for every internal restack.
#[allow(clippy::too_many_arguments)]
fn run_reraise_loop<C: Connection>(
    conn: Arc<C>,
    root: Window,
    me: Window,
    atoms: AtomCollection,
    stacking_atom: u32,
    target_rect: Rect,
    stop: Arc<AtomicBool>,
) {
    let mut last_raise = Instant::now() - RERAISE_MIN_INTERVAL;

    // A transient X error here (xfwm4 fighting us for z-order, a stale
    // window ID mid-restack, ...) must not kill this thread -- that would
    // silently and permanently disable re-raising for the rest of the
    // process's life, which is worse than just skipping one attempt and
    // trying again on the next trigger. Log and keep going.
    let try_raise = |last_raise: &mut Instant| {
        let now = Instant::now();
        if now.duration_since(*last_raise) < RERAISE_MIN_INTERVAL {
            return;
        }
        if let Err(e) = raise_above_desktop_owner(&*conn, root, me, &atoms, target_rect) {
            eprintln!("x11shim: re-raise above xfdesktop failed (will retry): {e}");
        }
        *last_raise = now;
    };

    while !stop.load(Ordering::Relaxed) {
        match conn.poll_for_event() {
            Ok(Some(Event::PropertyNotify(ev))) => {
                if ev.atom == stacking_atom {
                    try_raise(&mut last_raise);
                }
            }
            Ok(Some(_)) => {}
            Ok(None) => {
                thread::sleep(Duration::from_millis(50));
                if Instant::now().duration_since(last_raise) >= PERIODIC_RERAISE_INTERVAL {
                    try_raise(&mut last_raise);
                }
            }
            Err(_) => break,
        }
    }
}
