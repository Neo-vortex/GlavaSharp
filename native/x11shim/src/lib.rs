//! x11shim (Rust) — the X11-side half of GlavaSharp's `--desktop` mode.
//! Same shape as native/pwshim: a tiny flat C ABI (start/stop), the actual
//! protocol work done in Rust instead of hand-rolled Xlib P/Invoke from C#.
//!
//! GLFW (via OpenTK on the C# side) owns window/context creation; this crate
//! only takes the resulting X11 window ID and, on its own connection to the
//! X server, does the EWMH work GLFW has no concept of:
//!
//!   - marks the window `_NET_WM_WINDOW_TYPE_DESKTOP`
//!   - adds `_NET_WM_STATE_BELOW` + `_NET_WM_STATE_STICKY` ("pinned"/"below",
//!     the same two states GLava's own `env_Xfwm4.glsl` requests)
//!   - strips decorations via `_MOTIF_WM_HINTS`
//!   - positions/sizes the window -- by default the whole screen, or a
//!     specific rect when the caller passes one (GLava's `setgeometry`
//!     equivalent for desktop mode, see `--desktop-geometry` in Program.cs)
//!     -- and restacks it below
//!     whichever mapped window looks like the desktop-icon owner (its
//!     `WM_CLASS` contains "xfdesktop"), not just a sibling-less `Below`
//!   - gives the window an empty SHAPE-extension input region, making it
//!     fully click-through unconditionally. This is the actual fix for "the
//!     window must not intercept clicks meant for desktop icons": on a live
//!     xfwm4/XFCE session, even the explicit xfdesktop-relative restack
//!     above didn't reliably land the window *underneath* xfdesktop in
//!     `_NET_CLIENT_LIST_STACKING` -- xfwm4 appears to keep xfdesktop pinned
//!     at the true bottom regardless of what other desktop-typed clients
//!     request. Click-through via SHAPE sidesteps that fight entirely: it
//!     doesn't matter where the WM actually stacks the window, nothing on
//!     it ever receives input.
//!   - watches the root window's `_NET_CLIENT_LIST_STACKING` for changes and
//!     re-lowers on restack, the same "keep re-lowering" behavior GLava
//!     relies on to stay behind desktop icons if something restacks it
//!     (a WM restart, another desktop-layer client remapping, etc.) -- this
//!     is now a best-effort visual-ordering nicety rather than what click
//!     safety depends on
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
        _NET_WM_WINDOW_TYPE_DESKTOP,
        _NET_WM_STATE,
        _NET_WM_STATE_BELOW,
        _NET_WM_STATE_STICKY,
        _MOTIF_WM_HINTS,
        _NET_CLIENT_LIST_STACKING,
    }
}

// MWM_HINTS_DECORATIONS bit in the Motif hints `flags` field; a `decorations`
// value of 0 with this bit set means "no decorations at all".
const MWM_HINTS_DECORATIONS: u32 = 1 << 1;

// Minimum gap between re-lower attempts triggered by stacking-change events.
// Without this, our own configure_window(BELOW) call changes
// _NET_CLIENT_LIST_STACKING, which re-triggers the listener that just fired
// it -- a tight feedback loop. 200ms is imperceptible for "stay behind the
// icons" purposes and comfortably breaks that loop.
const RELOWER_MIN_INTERVAL: Duration = Duration::from_millis(200);

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

    conn.change_property32(
        PropMode::REPLACE,
        window,
        atoms._NET_WM_WINDOW_TYPE,
        AtomEnum::ATOM,
        &[atoms._NET_WM_WINDOW_TYPE_DESKTOP],
    )?
    .check()?;

    conn.change_property32(
        PropMode::REPLACE,
        window,
        atoms._NET_WM_STATE,
        AtomEnum::ATOM,
        &[atoms._NET_WM_STATE_BELOW, atoms._NET_WM_STATE_STICKY],
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
    // this is independent of and more reliable than window-stacking order,
    // since (per live testing against xfwm4) a plain client restack request
    // doesn't reliably get the window placed strictly *below* xfdesktop's
    // own windows; xfwm4 appears to keep xfdesktop pinned at the true
    // bottom regardless. A visualizer has no reason to receive input at all.
    conn.shape_rectangles(SO::SET, SK::INPUT, ClipOrdering::UNSORTED, window, 0, 0, &[])?
        .check()?;

    // ConfigureWindow's sibling/stack_mode fields require `sibling` to
    // actually be an X11 sibling (same parent) of the window being
    // configured -- since a reparenting WM puts each client's *frame*, not
    // the raw client window, directly under root, both windows have to be
    // walked up to their frame before this is a valid request. See
    // `toplevel_ancestor` / `find_desktop_owner_sibling` below.
    let my_toplevel = toplevel_ancestor(&*conn, screen.root, window)?;
    lower_below_desktop_owner(&*conn, screen.root, my_toplevel, window, &atoms)?;

    // Watch the root window for stacking changes so we can re-lower
    // ourselves if something restacks us above the desktop-icon layer.
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
            run_relower_loop(
                conn_for_thread,
                screen.root,
                my_toplevel,
                window,
                atoms,
                stacking_atom,
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

/// Finds the topmost mapped window in `_NET_CLIENT_LIST_STACKING` whose
/// `WM_CLASS` mentions "xfdesktop" (case-insensitively covers both the
/// instance and class parts of the property in one string), and returns
/// *its* top-level/frame ancestor -- the actual valid `sibling` for a
/// ConfigureWindow restack. Best-effort: any protocol error along the way
/// just means "couldn't find it," not a hard failure.
fn find_desktop_owner_toplevel<C: Connection>(
    conn: &C,
    root: Window,
    me: Window,
    atoms: &AtomCollection,
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
        if String::from_utf8_lossy(&prop.value)
            .to_lowercase()
            .contains("xfdesktop")
        {
            best = Some(client); // last match wins -> topmost xfdesktop window in the stack
        }
    }

    toplevel_ancestor(conn, root, best?).ok()
}

/// Restacks `my_toplevel` directly below the desktop-icon owner's frame
/// when we can find one, falling back to a plain sibling-less `Below`
/// restack (bottom of our own stacking layer) otherwise.
fn lower_below_desktop_owner<C: Connection>(
    conn: &C,
    root: Window,
    my_toplevel: Window,
    me: Window,
    atoms: &AtomCollection,
) -> Result<(), Box<dyn std::error::Error>> {
    let aux = match find_desktop_owner_toplevel(conn, root, me, atoms) {
        Some(sibling) if sibling != my_toplevel => {
            ConfigureWindowAux::new().sibling(sibling).stack_mode(StackMode::BELOW)
        }
        _ => ConfigureWindowAux::new().stack_mode(StackMode::BELOW),
    };
    conn.configure_window(my_toplevel, &aux)?.check()?;
    Ok(())
}

/// Polls (rather than blocking on `wait_for_event`) so `stop` can be
/// observed promptly without needing a self-pipe/eventfd just to interrupt a
/// blocking socket read -- 50ms poll granularity is unnoticeable for both
/// shutdown latency and "stay behind the icons" responsiveness.
#[allow(clippy::too_many_arguments)]
fn run_relower_loop<C: Connection>(
    conn: Arc<C>,
    root: Window,
    my_toplevel: Window,
    me: Window,
    atoms: AtomCollection,
    stacking_atom: u32,
    stop: Arc<AtomicBool>,
) {
    let mut last_lower = Instant::now() - RELOWER_MIN_INTERVAL;

    while !stop.load(Ordering::Relaxed) {
        match conn.poll_for_event() {
            Ok(Some(Event::PropertyNotify(ev))) => {
                if ev.atom == stacking_atom {
                    let now = Instant::now();
                    if now.duration_since(last_lower) >= RELOWER_MIN_INTERVAL {
                        if lower_below_desktop_owner(&*conn, root, my_toplevel, me, &atoms).is_err() {
                            break;
                        }
                        last_lower = now;
                    }
                }
            }
            Ok(Some(_)) => {}
            Ok(None) => thread::sleep(Duration::from_millis(50)),
            Err(_) => break,
        }
    }
}
