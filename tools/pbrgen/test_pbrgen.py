"""Tests for the offline PBR prototype.

These assert the properties the passes are *supposed* to have, not their exact
output. Pinning exact pixel values would mean every tuning change breaks the
suite, and the constants in pbrgen.py are explicitly expected to be re-tuned
against real textures.

Run:  python3 -m pytest tools/pbrgen/ -q
"""

from __future__ import annotations

import numpy as np
import pytest
from PIL import Image

import make_samples
import pbrgen


# --------------------------------------------------------------------------
# Colour space
# --------------------------------------------------------------------------

def test_srgb_linear_round_trip():
    values = np.linspace(0.0, 1.0, 64)
    assert np.allclose(pbrgen.linear_to_srgb(pbrgen.srgb_to_linear(values)), values, atol=1e-9)


def test_luminance_of_white_is_one():
    white = np.ones((2, 2, 3))
    assert np.allclose(pbrgen.luminance(white), 1.0)


# --------------------------------------------------------------------------
# Normal pass
# --------------------------------------------------------------------------

def test_flat_surface_gives_flat_normal():
    """A uniform texture has no slope anywhere, so every normal points straight out."""
    normal = pbrgen.generate_normal_from_luminance(np.full((8, 8), 0.5))
    assert np.allclose(normal[..., 0], 0.5)
    assert np.allclose(normal[..., 1], 0.5)
    assert np.allclose(normal[..., 2], 1.0)


def test_normals_are_unit_length():
    rng = np.random.default_rng(0)
    normal = pbrgen.generate_normal_from_luminance(rng.random((16, 16)))
    decoded = normal * 2.0 - 1.0
    assert np.allclose(np.linalg.norm(decoded, axis=-1), 1.0, atol=1e-9)


def test_normal_encoding_stays_in_range():
    rng = np.random.default_rng(1)
    normal = pbrgen.generate_normal_from_luminance(rng.random((16, 16)), strength=50.0)
    assert normal.min() >= 0.0 and normal.max() <= 1.0


def test_x_slope_tilts_normal_against_the_gradient():
    """Height rising to the right must tilt the normal left (encoded red < 0.5)."""
    lum = np.tile(np.linspace(0.0, 1.0, 16), (16, 1))
    normal = pbrgen.generate_normal_from_luminance(lum, tiling=False)
    assert normal[8, 8, 0] < 0.5


def test_y_slope_uses_opengl_convention():
    """Height rising *down the image* is a downward world slope: encoded green > 0.5.

    This is the axis most often flipped between the OpenGL and DirectX
    conventions, and getting it wrong produces lighting that looks plausible
    until the sun moves.
    """
    lum = np.tile(np.linspace(0.0, 1.0, 16)[:, None], (1, 16))
    normal = pbrgen.generate_normal_from_luminance(lum, tiling=False)
    assert normal[8, 8, 1] > 0.5


def test_tiling_wraps_instead_of_clamping():
    """A seam at the wrap boundary means visible wrong normals at every block edge."""
    lum = np.zeros((8, 8))
    lum[:, 0] = 1.0

    wrapped = pbrgen.generate_normal_from_luminance(lum, tiling=True)
    clamped = pbrgen.generate_normal_from_luminance(lum, tiling=False)

    # The last column neighbours the bright first column only when wrapping.
    assert not np.allclose(wrapped[:, -1, 0], 0.5)
    assert np.allclose(clamped[:, -1, 0], 0.5)


# --------------------------------------------------------------------------
# Roughness pass
# --------------------------------------------------------------------------

def test_uniform_texture_is_at_the_roughness_floor():
    roughness = pbrgen.generate_roughness_from_variance(np.full((8, 8), 0.5))
    assert np.allclose(roughness, pbrgen.ROUGHNESS_FLOOR)


def test_noisy_texture_is_rougher_than_smooth_one():
    rng = np.random.default_rng(2)
    noisy = pbrgen.generate_roughness_from_variance(rng.random((32, 32)))
    smooth = pbrgen.generate_roughness_from_variance(np.full((32, 32), 0.5))
    assert noisy.mean() > smooth.mean()


def test_roughness_stays_in_range():
    rng = np.random.default_rng(3)
    for scale in (0.0, 0.01, 1.0, 100.0):
        roughness = pbrgen.generate_roughness_from_variance(rng.random((16, 16)) * scale)
        assert roughness.min() >= 0.0 and roughness.max() <= 1.0
        assert not np.isnan(roughness).any()


def test_variance_never_goes_negative_on_uniform_input():
    """E[x^2]-E[x]^2 can cancel below zero; sqrt of that is a NaN in the PNG."""
    roughness = pbrgen.generate_roughness_from_variance(np.full((16, 16), 0.123456789))
    assert not np.isnan(roughness).any()


def test_box_mean_of_constant_is_that_constant():
    for radius in (1, 2, 3):
        assert np.allclose(pbrgen._box_mean(np.full((16, 16), 0.4), radius, True), 0.4)


def test_box_mean_matches_a_naive_implementation():
    """Guards the summed-area-table optimisation against the obvious version."""
    rng = np.random.default_rng(4)
    a = rng.random((12, 12))
    radius = 2

    padded = np.pad(a, radius, mode="wrap")
    naive = np.empty_like(a)
    for y in range(a.shape[0]):
        for x in range(a.shape[1]):
            naive[y, x] = padded[y:y + 2 * radius + 1, x:x + 2 * radius + 1].mean()

    assert np.allclose(pbrgen._box_mean(a, radius, True), naive)


# --------------------------------------------------------------------------
# Spec mask pass
# --------------------------------------------------------------------------

def test_saturated_colour_is_less_specular_than_neutral():
    """Pigment reads as diffuse; neutral bright reads as polish or metal."""
    alpha = np.ones((8, 8))
    red = np.zeros((8, 8, 3))
    red[..., 0] = 0.8
    grey = np.full((8, 8, 3), 0.8)

    assert (pbrgen.generate_spec_mask_from_colour_average(grey, alpha).mean()
            > pbrgen.generate_spec_mask_from_colour_average(red, alpha).mean())


def test_dark_neutral_is_less_specular_than_bright_neutral():
    alpha = np.ones((8, 8))
    dark = pbrgen.generate_spec_mask_from_colour_average(np.full((8, 8, 3), 0.05), alpha)
    bright = pbrgen.generate_spec_mask_from_colour_average(np.full((8, 8, 3), 0.9), alpha)
    assert bright.mean() > dark.mean()


def test_transparent_pixels_get_no_specularity():
    rgb = np.full((8, 8, 3), 0.9)
    alpha = np.zeros((8, 8))
    alpha[:4] = 1.0

    spec = pbrgen.generate_spec_mask_from_colour_average(rgb, alpha)
    assert np.allclose(spec[4:], 0.0)
    assert spec[:4].max() > 0.0


def test_fully_transparent_texture_does_not_crash():
    spec = pbrgen.generate_spec_mask_from_colour_average(np.full((4, 4, 3), 0.5), np.zeros((4, 4)))
    assert np.allclose(spec, 0.0)


def test_inclusions_are_boosted_above_their_surroundings():
    """A small bright patch in a dull field should read as metal, not as field."""
    rgb = np.full((16, 16, 3), 0.30)
    rgb[7:9, 7:9] = 0.85  # 4/256 texels, well under SPEC_INCLUSION_MAX_AREA
    spec = pbrgen.generate_spec_mask_from_colour_average(rgb, np.ones((16, 16)))
    assert spec[8, 8] > spec[0, 0] * 2.0


def test_regions_are_segmented_not_smeared():
    """Two clearly different colours must not merge into one region."""
    rgb = np.zeros((8, 8, 3))
    rgb[:, :4] = 0.9
    rgb[:, 4:] = 0.1

    labels, count = pbrgen._label_colour_regions(rgb, np.ones((8, 8), dtype=bool),
                                                 pbrgen.SPEC_REGION_THRESHOLD, tiling=False)
    assert count == 2
    assert labels[0, 0] != labels[0, 7]


def test_spec_mask_stays_in_range():
    rng = np.random.default_rng(5)
    spec = pbrgen.generate_spec_mask_from_colour_average(rng.random((16, 16, 3)), np.ones((16, 16)))
    assert spec.min() >= 0.0 and spec.max() <= 1.0


# --------------------------------------------------------------------------
# End to end
# --------------------------------------------------------------------------

@pytest.fixture(scope="module")
def samples(tmp_path_factory):
    outdir = tmp_path_factory.mktemp("samples")
    make_samples.generate(outdir)
    return outdir


def test_sample_set_covers_the_milestone_categories(samples):
    """Phase 4's milestone names stone, metal ore and wood explicitly."""
    names = {p.stem for p in samples.glob("*.png")}
    assert {"stone", "iron_ore", "wood_planks"} <= names
    assert len(names) >= 10


def test_process_texture_produces_correctly_shaped_maps(samples):
    maps = pbrgen.process_texture(samples / "stone.png")
    assert maps.normal.shape == (maps.height, maps.width, 3)
    assert maps.roughness.shape == (maps.height, maps.width)
    assert maps.spec.shape == (maps.height, maps.width)


def test_process_texture_is_deterministic(samples):
    first = pbrgen.process_texture(samples / "granite.png")
    second = pbrgen.process_texture(samples / "granite.png")
    assert np.array_equal(first.normal, second.normal)
    assert np.array_equal(first.roughness, second.roughness)
    assert np.array_equal(first.spec, second.spec)


def test_no_nans_anywhere_in_the_sample_set(samples):
    """A NaN reaching _to_image silently becomes garbage in the PNG."""
    for path in sorted(samples.glob("*.png")):
        maps = pbrgen.process_texture(path)
        for name, array in (("normal", maps.normal), ("rough", maps.roughness), ("spec", maps.spec)):
            assert not np.isnan(array).any(), f"{path.stem}/{name} contains NaN"
            assert array.min() >= 0.0 and array.max() <= 1.0, f"{path.stem}/{name} out of range"


def test_milestone_materials_are_visibly_distinct(samples):
    """The Phase 4 milestone: stone, metal ore and wood must differ without hand authoring.

    This is the acceptance criterion from the implementation plan expressed as
    a test. If it fails, the constants need re-tuning - not the assertion.
    """
    stone = pbrgen.process_texture(samples / "stone.png")
    ore = pbrgen.process_texture(samples / "iron_ore.png")
    wood = pbrgen.process_texture(samples / "wood_planks.png")

    # Ore has bright neutral inclusions in a duller field, so it must read as
    # more specular than plain stone.
    assert ore.spec.max() > stone.spec.max()

    # The three must not be interchangeable on the combined signal.
    signatures = [
        (m.roughness.mean(), m.spec.mean())
        for m in (stone, ore, wood)
    ]
    for i in range(len(signatures)):
        for j in range(i + 1, len(signatures)):
            distance = max(abs(signatures[i][0] - signatures[j][0]),
                           abs(signatures[i][1] - signatures[j][1]))
            assert distance > 0.02, f"materials {i} and {j} are indistinguishable: {signatures}"


def test_uniform_textures_read_as_smoother_than_noisy_ones(samples):
    polished = pbrgen.process_texture(samples / "polished_metal.png")
    gravel = pbrgen.process_texture(samples / "gravel.png")
    assert polished.roughness.mean() < gravel.roughness.mean()


def test_write_maps_emits_three_greyscale_or_rgb_pngs(samples, tmp_path):
    maps = pbrgen.process_texture(samples / "sand.png")
    written = pbrgen.write_maps(maps, tmp_path / "out")

    assert len(written) == 3
    for path in written:
        assert path.exists()
        with Image.open(path) as image:
            assert image.size == (maps.width, maps.height)


def test_contact_sheet_has_one_row_per_texture(samples, tmp_path):
    paths = sorted(samples.glob("*.png"))[:3]
    all_maps = [pbrgen.process_texture(p) for p in paths]
    sheet = pbrgen.build_contact_sheet(all_maps, paths, scale=2)

    # Four panels wide, one row per texture.
    assert sheet.width == sheet.height // len(paths) * 4


def test_cli_runs_over_a_directory(samples, tmp_path, capsys):
    exit_code = pbrgen.main([
        str(samples),
        "--outdir", str(tmp_path / "out"),
        "--contact-sheet", str(tmp_path / "sheet.png"),
        "--stats", str(tmp_path / "stats.json"),
    ])

    assert exit_code == 0
    assert (tmp_path / "sheet.png").exists()
    assert (tmp_path / "stats.json").exists()
    assert len(list((tmp_path / "out").glob("*_normal.png"))) >= 10


def test_cli_reports_failure_when_given_nothing(tmp_path, capsys):
    assert pbrgen.main([str(tmp_path / "missing")]) == 1
