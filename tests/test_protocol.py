"""Unit tests for Logitech G300s protocol serializers and parsers."""

import pytest
from g300s.constants import (
    REPORT_ACTIVE_STATE,
    REPORT_LIVE_LED,
    REPORT_LIVE_RATE,
    REPORT_INPUT_EVENT,
    SPECIAL_ACTIONS
)
from g300s.protocol import (
    parse_f0_state,
    make_set_active_profile_cmd,
    make_set_dpi_level_cmd,
    parse_f1_led,
    make_f1_led_cmd,
    parse_f2_rate,
    make_f2_rate_cmd,
    parse_profile,
    serialize_profile,
    parse_input_event
)
from g300s.exceptions import G300sValidationError


def test_parse_f0_state():
    # profile=1 (0b0001 << 4 = 0x10), dpi_level=2 (0b10 << 1 = 0x04), not busy
    # raw_byte1 = 0x14
    raw = bytes([0xF0, 0x14, 0x00, 0x00])
    res = parse_f0_state(raw)
    assert res["profile"] == 1
    assert res["dpi_level"] == 2
    assert res["is_busy"] is False

    # Busy bit set (bit 3 = 0x08)
    raw_busy = bytes([0xF0, 0x1C, 0x00, 0x00])
    res_busy = parse_f0_state(raw_busy)
    assert res_busy["is_busy"] is True


def test_make_f0_commands():
    cmd_prof = make_set_active_profile_cmd(2)
    assert cmd_prof == bytes([0xF0, 0x80 | (2 << 4), 0x00, 0x00])

    cmd_dpi = make_set_dpi_level_cmd(3)
    assert cmd_dpi == bytes([0xF0, 0x40 | (3 << 1), 0x00, 0x00])

    with pytest.raises(G300sValidationError):
        make_set_active_profile_cmd(3)

    with pytest.raises(G300sValidationError):
        make_set_dpi_level_cmd(4)


def test_f1_live_led():
    # 0xF1: byte 1 = [0:5, b:1, g:1, r:1]
    # Cyan: r=0, g=1, b=1 -> 0b110 = 6
    raw = bytes([0xF1, 0x06])
    r, g, b = parse_f1_led(raw)
    assert (r, g, b) == (0, 1, 1)

    cmd = make_f1_led_cmd(1, 0, 1)  # Magenta: 0b101 = 5
    assert cmd == bytes([0xF1, 0x05])


def test_f2_live_rate():
    # 0xF2: byte 1 = 0x04 | (rate_code & 0x03)
    # Rate code 0 = 1000Hz -> 0x04
    # Rate code 1 = 125Hz  -> 0x05
    # Rate code 2 = 250Hz  -> 0x06
    # Rate code 3 = 500Hz  -> 0x07
    raw_1000 = bytes([0xF2, 0x04])
    assert parse_f2_rate(raw_1000) == 1000

    raw_500 = bytes([0xF2, 0x07])
    assert parse_f2_rate(raw_500) == 500

    cmd = make_f2_rate_cmd(1000)
    assert cmd == bytes([0xF2, 0x04])

    cmd_250 = make_f2_rate_cmd(250)
    assert cmd_250 == bytes([0xF2, 0x06])

    with pytest.raises(G300sValidationError):
        make_f2_rate_cmd(2000)


def test_input_event_parser():
    # Report 0x80: [0x80, state, 0x00, 0x00, action]
    # DPI Up pressed
    raw_pressed = bytes([0x80, 0x01, 0x00, 0x00, 0x0A])
    evt = parse_input_event(raw_pressed)
    assert evt["action_code"] == 0x0A
    assert evt["action"] == "dpi-up"
    assert evt["pressed"] is True

    # Sniper released
    raw_released = bytes([0x80, 0x00, 0x00, 0x00, 0x0E])
    evt2 = parse_input_event(raw_released)
    assert evt2["action_code"] == 0x0E
    assert evt2["action"] == "dpi-alternate"
    assert evt2["pressed"] is False
