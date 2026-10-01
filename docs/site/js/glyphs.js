// What the rain of the show pages and the still frame of the calm pages draw with: the glyphs, the font stack, the seeded
// generator and the check that the system has a katakana font. Each script of the site (lib/bundle.mjs) joins its own copy.

export const CJK = '"Noto Sans CJK JP","Noto Sans JP","Hiragino Sans","Hiragino Kaku Gothic ProN","Yu Gothic",Meiryo,"MS Gothic",sans-serif';
export const KATAKANA = Array.from({ length: 56 }, (_, i) => String.fromCharCode(0xFF66 + i));
export const GLYPHS = [...KATAKANA, ...'0123456789'];                  // 66, like the ModDB art
export const FALLBACK_GLYPHS = [...'0123456789:.=*+-<>|'];              // when the system has no katakana: no empty boxes

/** A small seeded generator: the same rain on every load, and a test that can say so. */
export function mulberry32(seed) {
  let a = seed | 0;
  return () => {
    a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Whether a katakana glyph draws as something: it must not look like the one that stands for a missing glyph. */
export function hasKatakana() {
  const cv = document.createElement('canvas');
  cv.width = cv.height = 24;
  const x = cv.getContext('2d', { willReadFrequently: true });
  if (!x) return false;
  const ink = (ch) => {
    x.clearRect(0, 0, 24, 24); x.font = `17px ${CJK}`; x.textAlign = 'center'; x.textBaseline = 'middle';
    x.fillStyle = '#fff'; x.fillText(ch, 12, 12);
    return Array.prototype.join.call(x.getImageData(0, 0, 24, 24).data, '');
  };
  return ink('\uFF71') !== ink('\uFFFF') && ink('\uFF71') !== ink('\uFF9D');
}
