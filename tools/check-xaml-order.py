#!/usr/bin/env python3
"""Flag a Grid whose children come before its <Grid.RowDefinitions>/<Grid.ColumnDefinitions>.
In an Uno DataTemplate that drew nothing at all (blank Calls rows, 4855229)."""
import sys, pathlib, xml.etree.ElementTree as ET
bad = 0
for path in sys.argv[1:] or pathlib.Path("src").rglob("*.xaml"):
    if "/obj/" in str(path) or "/bin/" in str(path):
        continue
    for el in ET.parse(path).iter():
        seen_child = None
        tag = el.tag.split("}")[-1]
        for c in el:
            ctag = c.tag.split("}")[-1] if isinstance(c.tag, str) else ""
            if ctag in ("Grid.RowDefinitions", "Grid.ColumnDefinitions"):
                if seen_child is not None:
                    bad += 1
                    print(f"{path}: <{tag}> child <{seen_child}> precedes <{ctag}>")
            elif ctag and "." not in ctag:
                seen_child = ctag
sys.exit(1 if bad else 0)
