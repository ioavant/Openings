# Openings — product facts

Plain-text source of truth for **all** product copy: the website, the Autodesk
App Store listing, and anything else. No HTML, no store-specific formatting —
each consumer styles this its own way.

- The App Store submission is `appstore-listing.md`, filled in from this file.
  When a fact changes, change it **here first**, then re-derive that one.
- Technical facts here are read from the code: `version.json`, `branding.props`,
  `Openings/App.cs`, `OpeningRun.cs`, `OpeningPlanner.cs`, `OpeningMatcher.cs`,
  `OpeningFamily.cs`, `OpeningTracking.cs`, `OpeningParams.cs`, `Installer/Product.wxs`.

---

## Identity

- **Name:** Openings
- **Publisher:** Vixeldorf
- **Category:** Revit add-in — MEP coordination / builder's work openings
- **Current version:** 1.0.0
- **Platform:** Autodesk Revit 2022 through 2027 (Windows, 64-bit)
- **Interface language:** English
- **Website:** https://www.vixeldorf.com
- **Support:** yoav@vixeldorf.com, answered within 5 business days
- **Source:** https://github.com/ioavant/Openings

## One-liner

Openings where your ducts and pipes cross the architect's walls and slabs — placed
once, and kept right as the model changes.

## Short description (one sentence)

Openings finds where ducts, pipes, cable trays, conduits and shafts cross walls,
beams and slabs — in linked models or your own — places a sized opening at every
crossing, and keeps those openings in step as the MEP model moves on.

## What it does

Pick which models the walls and slabs come from — any loaded link, the current
model, or both — and run. Every crossing visible in the active view, or only the
MEP elements you selected, gets an opening: the full section of the element plus
insulation if you want it, plus a margin on each side, as deep as the host is
thick. Round where a single round duct or pipe crosses square on, rectangular
otherwise. Elements that sit close together can be merged into one opening.

The openings are not a one-off export. Each one remembers the host and the MEP
elements it was made for, so the next run updates it in place — same element,
same Opening ID, tags kept — instead of drawing a second one beside it. Move a
duct and its opening follows; delete the duct and the opening goes with it;
merge two ducts and their openings merge too.

Your own decisions are respected. An opening you edit, move or resize by hand is
locked and left alone by later runs, until the MEP it serves actually moves. An
opening you delete stays deleted. A pinned opening is never touched.

## Feature bullets

- Walls, beams, floors, roofs and ceilings from any loaded link or from the current model
- Ducts, pipes, cable trays and conduits, with their fittings and accessories if you choose
- ShaftMaster shafts: slab openings that follow the shaft's own profile, any shape
- Size = element section + optional insulation + margin per side; full host depth
- Round or rectangular automatically; nearby openings merged within a gap you set
- Two ways to model an opening, switchable at any time:
  - Generic Model (DirectShape) — any shape, nothing to load
  - your own face-based family (one for rectangles, one for circles), with a parameter mapping —
    the families are only placed and given values, never edited
- Convert existing openings between the two, for a selection or everything in the view
- A palette that stays open while you work: every opening in the project with ID, host, shape,
  size, level and height above the floor below; select, show, delete, lock from the list
- Edit an opening by sketch: width and position in plan, height in a section through the wall,
  the whole outline of a slab opening
- One click for a temporary section through a wall opening
- Height above the finished floor found by looking down from the opening, in the link or the model;
  set your own floor when the automatic one is not the one you mean
- Lock: a run over the whole view leaves locked openings alone; a run on selected elements still updates them
- Openings follow moved or resized MEP elements, and are removed when their MEP elements are deleted
- Openings placed by hand from the chosen family are picked up and tracked like the others
- Resizing a family opening with its grips updates its data straight away
- Selected MEP + host pairs that do not quite touch (a wall stopped short of the slab) still get an opening
- Every opening carries its data in shared parameters for tags and schedules: Opening ID, width, height,
  length, diameter, depth, bottom and centre heights from the floor, from the Project Base Point and in
  shared coordinates, level, host, MEP elements, shape

## How a run decides

- Nothing selected: every MEP element of the chosen categories visible in the active view.
- MEP elements selected: only those (and the other elements sharing an opening with them).
- Hosts are always the walls and slabs visible in the active view, in the models ticked in the palette.
- A crossing that already has an opening updates it; an opening whose crossing is gone is removed.

## Data stored in the project

- Shared parameters on the opening elements (Generic Models), bound automatically on first run.
- A small key on each opening (DirectShape application data, or Extensible Storage on a family instance)
  holding its host, its MEP elements and where they were — this is how re-runs recognise it.
- A list of openings the user deleted, on one DataStorage element.
- No external database; nothing outside the .rvt. Settings are per user in %AppData%\Vixeldorf\Openings.
- A project opened on a machine without the add-in opens without warnings.

## Who it is for

MEP designers and BIM coordinators who issue builder's work / provision-for-void
openings to the architect and structural engineer, and have to keep them current
through every revision of the services model.

## Supported versions and install

- Revit 2022, 2023, 2024 (a .NET Framework 4.8 build) and Revit 2025, 2026, 2027 (a .NET 8 build).
- One MSI. Files go to C:\ProgramData\Vixeldorf\Openings (2025+ build in the net8 subfolder).
- Manifests: C:\ProgramData\Autodesk\Revit\Addins\<year> for 2022-2026,
  C:\Program Files\Autodesk\Revit\Addins\2027 for 2027. The installer lets you choose the years.
- Ribbon: Vixeldorf tab → Openings panel → Openings.
- Uninstall: Windows Settings → Apps → Vixeldorf Openings.

## Known limitations (true as of 1.0.0)

- Openings are coordination elements: they do not cut walls or slabs inside a linked model.
- Picking a host inside a link for a "selected pair" needs Revit 2023 or later.
- In Revit 2022 and 2023, link elements are taken from the view's visible volume; elements or categories
  of a link hidden in the view by other means are still considered.
- Families must be face-based Generic Models whose insertion point is the middle of the opening on the
  host face, with the void cutting Depth into the host. Free shapes stay Generic Models.
- Converting an opening gives it a new ElementId, so tags on it have to be placed again.
- Changes made while the add-in is not loaded (another machine without it) are caught up when the palette
  is next opened.

## Version history

- **1.0.0** — first release: openings as Generic Models or face-based families with conversion between
  them, sketch editing, temporary wall sections, Lock, height from the floor (automatic or set by hand),
  openings that follow moved MEP elements and are removed with deleted ones, adoption of hand-placed
  family openings.
