# Openings — Autodesk App Store listing (Vixeldorf build, v1.0.0)

Derived from `product-facts.md`; change facts there first. Character limits are
Autodesk's hard cutoffs — counts are noted per field.

---

## App Name

Openings

## App Short Description

Places sized openings wherever ducts, pipes, cable trays and shafts cross walls and slabs from links or your model, and keeps them updated as the MEP model changes.

## App Description

Openings finds every place where your services cross the building structure and puts a correctly sized opening there - then keeps those openings right through every revision of the MEP model.

Choose where the walls and slabs come from - any loaded link, the current model, or both - and run. Every crossing visible in the active view, or only the elements you selected, gets an opening: the full section of the duct, pipe, tray or conduit, plus insulation if you want it, plus a margin on each side, as deep as the host is thick. A single round element crossing square on gets a round opening; everything else is rectangular. Nearby openings can be merged into one.

Run it again tomorrow and nothing is duplicated. Each opening remembers its host and its MEP elements, so it is updated in place - same element, same Opening ID, tags kept. Move a duct and its opening follows. Delete the duct and the opening goes too. Your own decisions hold: an opening you edit or move by hand is locked and left alone until its duct actually moves, an opening you delete stays deleted, and a pinned opening is never touched.

Features:
- Walls, beams, floors, roofs and ceilings from links or the current model
- Ducts, pipes, cable trays, conduits, their fittings and accessories, and ShaftMaster shafts (any outline)
- Openings as Generic Models (any shape) or as your own face-based families for rectangles and circles, with a parameter mapping - convert between the two at any time
- A palette that stays open while you work: every opening with ID, host, shape, size, level and height above the floor below
- Edit by sketch: width and position in plan, height in a section, the full outline of a slab opening
- Temporary section through a wall opening in one click
- Height above the finished floor found automatically, or set by hand
- Lock, pin and delete are respected by every later run
- Resizing a family opening with its grips updates its data at once; family openings placed by hand are picked up and tracked
- Shared parameters for tags and schedules: Opening ID, sizes, depth, heights from floor, from Project Base Point and in shared coordinates, level, host, MEP elements

For MEP designers and BIM coordinators who issue builder's work openings to the architect and structural engineer.

Revit 2022 - 2027.

## App Version

**Version Number:** 1.0.0

**Version Description:** First release. Places and updates openings where MEP elements cross walls, beams and slabs in links or the current model, as Generic Models or face-based families (with conversion between them). Includes sketch editing, temporary wall sections, Lock, automatic or manual height from the floor, openings that follow moved MEP elements and are removed with deleted ones, and pick-up of family openings placed by hand.

## General Usage Instructions

1. Open a plan, section or 3D view that shows the walls / slabs and the MEP elements (for slab openings a 3D view with a section box works best).
2. Vixeldorf tab → Openings panel → Openings. The Openings palette opens and stays open while you work.
3. At the top, tick the models the walls and slabs come from (links and / or the current model), and choose whether openings are placed as Generic Models or as a family.
4. First time: Options… - host and MEP categories, margin, insulation, round openings, merging, and (for families) the rectangular and round family types with their parameter mapping.
5. Select MEP elements to work on just those, or select nothing for everything visible in the view, then click Place Openings. A report lists what was created, updated and deleted.
6. Use the list to select, show, lock or delete openings; Edit to adjust one by sketch; Section for a temporary section through a wall opening; Convert to switch openings between Generic Model and family.
7. After MEP changes, run Place Openings again - existing openings are updated, not duplicated.

## Installation/Uninstallation

Run the Vixeldorf Openings MSI and choose the Revit versions (2022-2027). The add-in is installed to C:\ProgramData\Vixeldorf\Openings (Revit 2025 and later use the .NET 8 build in its net8 subfolder). A manifest is registered for each chosen version in C:\ProgramData\Autodesk\Revit\Addins\<year> - for Revit 2027 in C:\Program Files\Autodesk\Revit\Addins\2027, the only place Revit 2027 reads. Start Revit: the Openings button is on the Vixeldorf tab.

To uninstall, use Windows Settings → Apps (or Control Panel → Programs and Features) → Vixeldorf Openings. This removes the add-in and every manifest. Openings already in your projects stay as ordinary Generic Model elements.

## Support Information

Email <a href="mailto:yoav@vixeldorf.com">yoav@vixeldorf.com</a>.<br>
Please include your Revit version and build, the Openings version (shown in the palette title), what you did and what you expected, and a screenshot of the report or error if there is one.<br>
We answer within 5 business days.

## Additional Information

<b>Supported:</b> Revit 2022, 2023, 2024 (.NET Framework 4.8 build) and Revit 2025, 2026, 2027 (.NET 8 build).<br>
<b>Category:</b> MEP coordination.<br>
<b>Data:</b> everything lives in the project - shared parameters on the openings (bound automatically), a small key on each opening that lets later runs recognise it, and one DataStorage element listing openings you deleted. No external database. A project opened without the add-in opens without warnings.<br>
<b>Publisher:</b> <a href="https://www.vixeldorf.com">Vixeldorf</a>

## Known Issues

- Openings are coordination elements: they do not cut walls or slabs inside a linked model.<br>
- Choosing a host inside a link for a selected MEP + host pair needs Revit 2023 or later.<br>
- In Revit 2022 and 2023, link elements hidden in the view by overrides are still considered (the view's visible volume is used).<br>
- Families must be face-based Generic Models with the insertion point in the middle of the opening on the host face; free shapes stay Generic Models.<br>
- Converting an opening creates a new element, so its tags must be placed again.

## Learn More Url

https://www.vixeldorf.com
