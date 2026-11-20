#!/usr/bin/env python3
"""Reads Sefirah's AT-SPI tree through libatspi, the way screen readers and computer-use tools do.

usage: tools/atspi-check.py [--depth N] [--invoke NAME] [--listen SECONDS]
  --invoke NAME       run DoAction(0) on the first node named NAME, then print the tree again
  --listen SECONDS    register for object/focus events and print what Sefirah emits
"""
import argparse
import sys
import time
import warnings

import gi

gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib  # noqa: E402

warnings.filterwarnings("ignore", category=DeprecationWarning)  # get_action_name has no non-deprecated twin in PyGObject


def find_app(name):
    desktop = Atspi.get_desktop(0)
    for i in range(desktop.get_child_count()):
        app = desktop.get_child_at_index(i)
        if app is not None and app.get_name() == name:
            return app
    return None


def dump(node, depth, max_depth, out):
    try:
        name, role, count = node.get_name(), node.get_role_name(), node.get_child_count()
    except Exception as ex:  # a broken node must not hide the rest of the tree
        out.append("  " * depth + f"<error {ex}>")
        return
    actions = ""
    try:
        action = node.get_action_iface()
        if action is not None and action.get_n_actions() > 0:
            actions = " actions=" + ",".join(action.get_action_name(i) for i in range(action.get_n_actions()))
    except Exception:
        pass
    out.append("  " * depth + f"[{role}] {name!r}{actions}")
    if depth >= max_depth:
        return
    for i in range(count):
        child = node.get_child_at_index(i)
        if child is not None:
            dump(child, depth + 1, max_depth, out)


def find(node, name):
    if node.get_name() == name:
        return node
    for i in range(node.get_child_count()):
        child = node.get_child_at_index(i)
        if child is not None and (hit := find(child, name)) is not None:
            return hit
    return None


def tree(app, max_depth):
    out = []
    dump(app, 0, max_depth, out)
    return out


def listen(seconds):
    """Print object and focus events for SECONDS, the way a screen reader receives them."""
    events = []

    def on_event(*args):
        event = args[0]
        source = event.source if event.source else None
        try:
            name = source.get_name() if source else "?"
        except Exception:
            name = "?"
        try:
            app = source.get_application().get_name() if source else "?"
        except Exception:
            app = "?"
        data = event.any_data
        if isinstance(data, Atspi.Accessible):
            try:
                data = data.get_name() or repr(data)
            except Exception:
                data = repr(data)
        elif data is not None:
            data = repr(data)
        line = (f"{event.type} app={app} name={name!r} detail1={event.detail1} "
                f"detail2={event.detail2} any_data={data}")
        events.append(line)
        print(line, flush=True)

    listener = Atspi.EventListener.new(on_event)
    for event_type in ("object:state-changed", "object:property-change",
                       "object:children-changed", "focus:"):
        if not listener.register(event_type):
            print(f"failed to register {event_type}", file=sys.stderr)

    print(f"listening {seconds}s for object/focus events...", flush=True)
    loop = GLib.MainLoop()
    GLib.timeout_add_seconds(seconds, loop.quit)
    loop.run()
    print(f"received {len(events)} events")
    return 0 if events else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--depth", type=int, default=12)
    parser.add_argument("--invoke")
    parser.add_argument("--listen", type=int, metavar="SECONDS")
    args = parser.parse_args()

    if args.listen is not None:
        return listen(args.listen)

    app = find_app("Sefirah")
    if app is None:
        names = [(a.get_name(), a.get_role_name()) for a in Atspi.get_desktop(0)]
        print("Sefirah not found on the desktop; apps:", names)
        return 1

    print(f"app: name={app.get_name()!r} role={app.get_role_name()} children={app.get_child_count()}")
    before = tree(app, args.depth)
    print("\n".join(before))
    if not args.invoke:
        return 0

    target = find(app, args.invoke)
    if target is None:
        print(f"no node named {args.invoke!r}")
        return 1
    ok = target.get_action_iface().do_action(0)
    print(f"\nDoAction(0) on {args.invoke!r} -> {ok}")
    time.sleep(1.5)
    after = tree(app, args.depth)
    print("\n".join(after))
    print("\ntree changed:", before != after)
    return 0 if before != after else 1


if __name__ == "__main__":
    sys.exit(main())
