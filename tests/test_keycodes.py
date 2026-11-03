"""Unit tests for G300s keystroke parsing, modifiers, and high function keys."""

import pytest
from g300s.keycodes import (
    parse_keystroke,
    format_keystroke,
    MODIFIERS,
    KEY_CODES
)


def test_keystroke_parsing_simple():
    # Single key: 'a'
    mod, hid = parse_keystroke("a")
    assert mod == 0
    assert hid == 0x04

    # Single key: 'enter'
    mod, hid = parse_keystroke("enter")
    assert mod == 0
    assert hid == 0x28


def test_keystroke_parsing_combinations():
    # 'ctrl+c' -> LCTRL (0x01), 'c' (0x06)
    mod, hid = parse_keystroke("ctrl+c")
    assert mod == 0x01
    assert hid == 0x06

    # 'ctrl+shift+esc'
    mod, hid = parse_keystroke("ctrl+shift+esc")
    assert mod == (0x01 | 0x02)
    assert hid == 0x29


def test_high_function_keys():
    # F13 -> HID 0x68
    mod, hid = parse_keystroke("f13")
    assert mod == 0
    assert hid == 0x68

    # F24 -> HID 0x73
    mod, hid = parse_keystroke("f24")
    assert mod == 0
    assert hid == 0x73

    formatted = format_keystroke(0, 0x68)
    assert formatted.lower() == "f13"


def test_keystroke_roundtrip():
    test_cases = ["a", "f1", "f13", "space", "enter", "ctrl+c", "alt+tab"]
    for case in test_cases:
        mod, hid = parse_keystroke(case)
        formatted = format_keystroke(mod, hid)
        mod2, hid2 = parse_keystroke(formatted)
        assert (mod, hid) == (mod2, hid2)
