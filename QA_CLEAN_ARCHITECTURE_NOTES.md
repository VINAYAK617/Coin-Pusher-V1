# QA Clean Architecture Notes

## User Direction

- Build a fresh, chaos-free architecture instead of adding more patches.
- Design and implement the engine in a clean way.
- Use the current understanding of the game rules to make a fresh implementation that is easier to reason about and harder to break.
- Avoid blind retry loops. The planner and board realization should make smart deterministic choices and only fall back when a choice is genuinely infeasible.

## QA Rules Captured From Workbook

- Starting board must be a full 5x5 board with no empty cells and no stacked cells.
- New game must reset board state, spinner state, and all prior feature state.
- Bonus features are processed before coin push.
- Feature order: Extra Go / Prize Upgrade before Wheel, and Wheel last when multiple features are revealed.
- Spinner values are normal pushes 1-3 or full-column PUSH.
- Multiple PUSH columns in one turn must collect without missing or duplicating coins.
- Board rotates clockwise 90 degrees after every turn.
- Every coin color must remain a valid configured symbol.
- At least one winning prize track should complete on the final turn when all prize tracks are intended to complete.
- Wheel must not appear on the grid after the push function of the final turn.
- Top prize must be awarded as a direct win only on the final turn.
- Extra Go awards exactly one turn per reveal and must not create negative turns or reset prize state.
- At most three Extra Go feature symbols may be revealed per turn.
- Prize Upgrade applies sequentially, does not reset prior upgrades, never upgrades top prize, and must not exceed win-up-to limits.
- Wheel starts/stops consistently, applies stack values to matching coins, and stack values persist until collection.
- Max coin stack is 7.
- Wheel stack awards must not push any coin over its target/capacity.
- Coins should not become permanently stuck in rotation-stable positions.
- Public serialized tickets must replay independently with no empty board cells after spawns.

## Architecture Goal

The engine should be built as a deterministic ticket compiler:

1. Normalize prize request into a game contract.
2. Decide feature and near-miss policy from capacity constraints.
3. Allocate exact collections by spin with final-turn anchors.
4. Place feature tokens using deterministic windows and conflict rules.
5. Realize board/spawns from allocations.
6. Run independent verification over both internal plan and public JSON.

Retries should become exception paths for impossible contracts, not the normal planning strategy.
