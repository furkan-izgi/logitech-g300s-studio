"""Unit tests for G300s Profile model, serialization roundtrips, and validation."""

import pytest
from g300s.profiles import Profile
from g300s.exceptions import G300sValidationError

LIVE_PROFILE_RAW = bytes.fromhex(
    "f30500040607894001000002000003000000011c00011d0001000400000d00000c0000"
)


def test_profile_roundtrip():
    raw = bytearray(LIVE_PROFILE_RAW)
    p = Profile.from_raw(raw, index=0)
    assert p.index == 0
    assert p.rate == 1000
    assert p.led == (1, 0, 1)  # 0x05 = Magenta (R=1, G=0, B=1)
    assert p.default_dpi == 3
    assert p.dpis == [1000, 1500, 1750, 2250]
    assert p.buttons[0] == "mouse:1"
    assert p.buttons[1] == "mouse:2"
    assert p.buttons[2] == "mouse:3"
    assert p.buttons[7] == "profile-cycle"
    assert p.buttons[8] == "dpi-cycle"

    # Serialize back and verify exact byte match
    serialized = p.to_raw()
    assert len(serialized) == 35
    assert serialized == raw


def test_profile_validation():
    # Invalid rate
    p_invalid_rate = Profile(index=0, rate=333)
    with pytest.raises(G300sValidationError):
        p_invalid_rate.validate()

    # Invalid DPI
    p_invalid_dpi = Profile(index=0, dpis=[200, 1000, 1500, 2000])  # 200 not multiple of 250
    with pytest.raises(G300sValidationError):
        p_invalid_dpi.validate()

    # Invalid LED
    p_invalid_led = Profile(index=0, led=(2, 0, 0))
    with pytest.raises(G300sValidationError):
        p_invalid_led.validate()

    # Invalid index
    p_invalid_idx = Profile(index=5)
    with pytest.raises(G300sValidationError):
        p_invalid_idx.validate()


def test_profile_json_roundtrip():
    p = Profile(
        index=1,
        rate=1000,
        led=(1, 0, 0),
        dpis=[250, 500, 1000, 2000],
        default_dpi=0,
        buttons=["mouse:1", "mouse:2", "mouse:3", "key:f13", "key:ctrl+c", "none", "none", "none", "none"]
    )
    p_dict = p.to_dict()
    restored = Profile.from_dict(p_dict)
    assert restored.index == 1
    assert restored.rate == 1000
    assert restored.led == (1, 0, 0)
    assert restored.dpis == [250, 500, 1000, 2000]
    assert restored.buttons[3] == "key:f13"
    assert restored.buttons[4] == "key:ctrl+c"
