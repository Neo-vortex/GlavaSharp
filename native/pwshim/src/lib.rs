//! pwshim (Rust) — same flat C ABI as the C version of pw_shim, so the
//! C# side (PipeWireNative.cs) doesn't change shape, just gains new
//! entry points. The PipeWire mainloop + stream run on a dedicated OS
//! thread; pwshim_stop() signals it to quit via a pipewire::channel and
//! joins it.
//!
//! Two independent things live here:
//!   - pwshim_list_targets: a short-lived registry scan that lists
//!     available capture targets (sink monitors + source nodes) as JSON.
//!   - pwshim_start/stop: the actual capture stream, now accepting a
//!     target node id (-1 = default sink monitor, same as before).
//!
//! Verify against your installed `pipewire` crate version — the
//! stream/pod/registry APIs here target pipewire-rs 0.8.x and have
//! moved between minor versions in the past.

use std::cell::RefCell;
use std::ffi::{c_char, CString};
use std::os::raw::{c_int, c_uint};
use std::rc::Rc;
use std::thread::JoinHandle;
use std::time::Duration;

use pipewire as pw;
use pw::spa;
use pw::types::ObjectType;
use std::ffi::c_void;

pub type DataCallback = extern "C" fn(*const f32, c_uint, *mut c_void);

struct SendPtr(*mut c_void);
unsafe impl Send for SendPtr {}

pub struct ShimHandle {
    quit_tx: pw::channel::Sender<()>,
    join: Option<JoinHandle<()>>,
}

// ---------------------------------------------------------------------
// Target enumeration
// ---------------------------------------------------------------------

#[derive(Clone, Debug)]
struct TargetInfo {
    id: u32,
    name: String,
    description: String,
    kind: &'static str, // "sink-monitor" | "source"
}

fn escape_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            c if (c as u32) < 0x20 => {}
            c => out.push(c),
        }
    }
    out
}

/// Scans the PipeWire registry for ~400ms and returns a JSON array of
/// available capture targets. Free the result with `pwshim_free_string`.
#[no_mangle]
pub extern "C" fn pwshim_list_targets() -> *mut c_char {
    let targets = Rc::new(RefCell::new(Vec::<TargetInfo>::new()));

    let result = (|| -> Result<(), pw::Error> {
        pw::init();
        let mainloop = pw::main_loop::MainLoop::new(None)?;
        let context = pw::context::Context::new(&mainloop)?;
        let core = context.connect(None)?;
        let registry = core.get_registry()?;

        let targets_ref = targets.clone();
        let _listener = registry
            .add_listener_local()
            .global(move |obj| {
                if obj.type_ != ObjectType::Node {
                    return;
                }
                let props = match &obj.props {
                    Some(p) => p,
                    None => return,
                };
                let media_class = props.get("media.class").unwrap_or("");
                let kind = if media_class == "Audio/Sink" {
                    "sink-monitor"
                } else if media_class == "Audio/Source" || media_class == "Audio/Source/Virtual" {
                    "source"
                } else {
                    return;
                };
                let name = props.get("node.name").unwrap_or("").to_string();
                let description = props.get("node.description").unwrap_or(&name).to_string();
                targets_ref.borrow_mut().push(TargetInfo {
                    id: obj.id,
                    name,
                    description,
                    kind,
                });
            })
            .register();

        // Registry events arrive asynchronously; pump the loop briefly
        // with a timer instead of running forever.
        let ml = mainloop.clone();
        let timer = mainloop.loop_().add_timer(move |_| ml.quit());
        timer
            .update_timer(Some(Duration::from_millis(400)), None)
            .into_result()
            .map_err(|_| pw::Error::CreationFailed)?;
        mainloop.run();
        Ok(())
    })();

    if let Err(e) = result {
        eprintln!("pwshim_list_targets: {e}");
    }

    let mut json = String::from("[");
    let mut first = true;
    for t in targets.borrow().iter() {
        if !first {
            json.push(',');
        }
        first = false;
        json.push_str(&format!(
            "{{\"id\":{},\"kind\":\"{}\",\"name\":\"{}\",\"description\":\"{}\"}}",
            t.id,
            t.kind,
            escape_json(&t.name),
            escape_json(&t.description)
        ));
    }
    json.push(']');

    CString::new(json)
        .unwrap_or_else(|_| CString::new("[]").unwrap())
        .into_raw()
}

#[no_mangle]
pub extern "C" fn pwshim_free_string(s: *mut c_char) {
    if s.is_null() {
        return;
    }
    unsafe {
        let _ = CString::from_raw(s);
    }
}

// ---------------------------------------------------------------------
// Capture stream
// ---------------------------------------------------------------------

/// # Safety
/// `cb` must be a valid extern "C" function pointer that stays valid for
/// the lifetime of the stream (i.e. until pwshim_stop is called).
/// `user_data` is passed through opaquely and must be valid for the same
/// duration. `target_id` of -1 captures the default sink's monitor
/// (previous behaviour); any other value pins the stream to that
/// specific PipeWire node id, as returned by pwshim_list_targets.
#[no_mangle]
pub extern "C" fn pwshim_start(
    rate: c_uint,
    channels: c_uint,
    target_id: c_int,
    cb: DataCallback,
    user_data: *mut c_void,
) -> *mut c_void {
    let user_data = SendPtr(user_data);
    let (quit_tx, quit_rx) = pw::channel::channel::<()>();

    let join = std::thread::Builder::new()
        .name("glavasharp-pw".into())
        .spawn(move || {
            if let Err(e) = run_loop(rate, channels, target_id, cb, user_data, quit_rx) {
                eprintln!("pwshim: capture thread exited with error: {e}");
            }
        })
        .expect("failed to spawn PipeWire thread");

    let handle = Box::new(ShimHandle {
        quit_tx,
        join: Some(join),
    });
    Box::into_raw(handle) as *mut c_void
}

#[no_mangle]
pub extern "C" fn pwshim_stop(ctx: *mut c_void) {
    if ctx.is_null() {
        return;
    }
    let mut handle = unsafe { Box::from_raw(ctx as *mut ShimHandle) };
    let _ = handle.quit_tx.send(());
    if let Some(join) = handle.join.take() {
        let _ = join.join();
    }
}

fn run_loop(
    rate: c_uint,
    channels: c_uint,
    target_id: c_int,
    cb: DataCallback,
    user_data: SendPtr,
    quit_rx: pw::channel::Receiver<()>,
) -> Result<(), pw::Error> {
    pw::init();

    let mainloop = pw::main_loop::MainLoop::new(None)?;
    let context = pw::context::Context::new(&mainloop)?;
    let core = context.connect(None)?;

    let mainloop_for_quit = mainloop.clone();
    let _quit_listener = quit_rx.attach(mainloop.loop_(), move |()| {
        mainloop_for_quit.quit();
    });

    // Base stream properties, built directly (rather than via the
    // `properties!` macro) so we can conditionally add the target-object
    // key depending on -1-vs-specific-node selection. Stream::new() takes
    // this Properties value directly — there's no update_properties()
    // method on Stream in pipewire-rs 0.8.x to add it after the fact.
    let mut props = pw::properties::Properties::new();
    props.insert(*pw::keys::MEDIA_TYPE, "Audio");
    props.insert(*pw::keys::MEDIA_CATEGORY, "Capture");
    props.insert(*pw::keys::MEDIA_ROLE, "Music");

    if target_id < 0 {
        // Default: whatever's playing on the default sink ("what you hear"),
        // same target GLava/cava use via PulseAudio's monitor source.
        props.insert(*pw::keys::STREAM_CAPTURE_SINK, "true");
    } else {
        // Pin to a specific node id (sink monitor OR source) chosen via
        // --list-sinks/--sink on the C# side. Using the raw PipeWire key
        // string ("target.object", PW_KEY_TARGET_OBJECT) rather than
        // pw::keys::TARGET_OBJECT — that constant isn't exported in
        // pipewire-rs 0.8.0. Swap to the constant if your pinned version
        // does have it.
        props.insert("target.object", target_id.to_string());
    }

    let stream = pw::stream::Stream::new(&core, "GlavaSharp Capture", props)?;

    let user_data = user_data;
    let _listener = stream
        .add_local_listener_with_user_data(())
        .process(move |stream, _| {
            let Some(mut buffer) = stream.dequeue_buffer() else {
                return;
            };
            let datas = buffer.datas_mut();
            let Some(data) = datas.get_mut(0) else { return };

            let chunk_offset = data.chunk().offset() as usize;
            let chunk_size = data.chunk().size() as usize;
            let Some(slice) = data.data() else { return };
            if chunk_offset + chunk_size > slice.len() {
                return;
            }

            let valid = &slice[chunk_offset..chunk_offset + chunk_size];
            let n_samples = valid.len() / std::mem::size_of::<f32>();
            let ptr = valid.as_ptr() as *const f32;
            cb(ptr, n_samples as c_uint, user_data.0);
        })
        .register()?;

    let mut audio_info = spa::param::audio::AudioInfoRaw::new();
    audio_info.set_format(spa::param::audio::AudioFormat::F32LE);
    audio_info.set_rate(rate);
    audio_info.set_channels(channels);

    let obj = spa::pod::Object {
        type_: spa::sys::SPA_TYPE_OBJECT_Format,
        id: spa::sys::SPA_PARAM_EnumFormat,
        properties: audio_info.into(),
    };

    let values: Vec<u8> = spa::pod::serialize::PodSerializer::serialize(
        std::io::Cursor::new(Vec::new()),
        &spa::pod::Value::Object(obj),
    )
    .map_err(|_| pw::Error::CreationFailed)?
    .0
    .into_inner();

    let mut params = [spa::pod::Pod::from_bytes(&values).expect("valid POD bytes")];

    stream.connect(
        spa::utils::Direction::Input,
        None,
        pw::stream::StreamFlags::AUTOCONNECT
            | pw::stream::StreamFlags::MAP_BUFFERS
            | pw::stream::StreamFlags::RT_PROCESS,
        &mut params,
    )?;

    mainloop.run();
    Ok(())
}
