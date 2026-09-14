import SwiftUI

/// The spit mark, shown in the mic button while listening: a dotted face with sound-wave arcs coming
/// off it, the arcs lighting from the inside out as the voice gets louder.
///
/// Drawn dot by dot from `SpitMarkGrid` rather than shipped as an image. An image would bake in the
/// mark's off-white, which disappears on the light-mode bar — filling with the foreground style lets
/// the button's own colour decide, white on red while latched included. And the arcs have to be
/// addressable one by one to follow the voice, which a flat bitmap cannot offer.
struct SpitMark: View {
    /// The microphone level, on the same scale as the waveform's `HUDModel.levels`.
    var level: Float

    struct Dot: Equatable {
        let x: Int
        let y: Int
    }

    /// An arc the voice has not reached fades back rather than vanishing: the arcs are half of what
    /// makes the mark recognisable, and a face on its own at 15pt reads as a smudge.
    private static let unlit = 0.22

    /// Parts of the mark are at least this many empty columns apart. The face is dithered and full
    /// of single empty columns, which must not split it.
    private static let partGap = 3

    private static let size = SpitMarkGrid.rows.count
    private static let shape = parts(SpitMarkGrid.rows)
    // In grid units, built once. The mark redraws with every level update, ~47 times a second.
    private static let facePath = path(shape.face)
    private static let arcPaths = shape.arcs.map(path)

    var body: some View {
        let lit = Self.litArcs(level: level, of: Self.arcPaths.count)
        Canvas { context, canvas in
            let cell = min(canvas.width, canvas.height) / CGFloat(Self.size)
            context.scaleBy(x: cell, y: cell)
            context.fill(Self.facePath, with: .foreground)
            for (i, arc) in Self.arcPaths.enumerated() {
                var layer = context
                layer.opacity = i < lit ? 1 : Self.unlit
                layer.fill(arc, with: .foreground)
            }
        }
        .accessibilityHidden(true)
    }

    /// Splits the mark into its face — the first part from the left — and the arcs after it, inside
    /// first.
    static func parts(_ rows: [String]) -> (face: [Dot], arcs: [[Dot]]) {
        let grid = rows.map(Array.init)
        let width = grid.map(\.count).max() ?? 0
        var groups: [[Dot]] = []
        var current: [Dot] = []
        var emptyColumns = 0
        for x in 0..<width {
            let column = grid.indices.compactMap { y in
                x < grid[y].count && grid[y][x] == "#" ? Dot(x: x, y: y) : nil
            }
            if column.isEmpty {
                emptyColumns += 1
                continue
            }
            if emptyColumns >= partGap, !current.isEmpty {
                groups.append(current)
                current = []
            }
            current += column
            emptyColumns = 0
        }
        if !current.isEmpty { groups.append(current) }
        guard let face = groups.first else { return ([], []) }
        return (face, Array(groups.dropFirst()))
    }

    /// How many of `count` arcs a level lights. Rounded to the nearest arc, so the room's noise
    /// floor lights none and the mark stays still while nobody is talking.
    static func litArcs(level: Float, of count: Int) -> Int {
        guard level.isFinite, level > 0 else { return 0 }
        return Int((HUDView.loudness(level) * Float(count)).rounded())
    }

    private static func path(_ dots: [Dot]) -> Path {
        var path = Path()
        for dot in dots { path.addRect(CGRect(x: dot.x, y: dot.y, width: 1, height: 1)) }
        return path
    }
}
