// Writes stringify-vectors.json next to this script: what JSON.stringify writes for each input.
import { writeFileSync } from 'node:fs';

// Every code unit outside printable ASCII is written as an upper-case \u escape, and "/" as "\/", so a reader
// that copies source text instead of decoding and re-escaping it fails.
function literal(text) {
  let out = '"';
  for (let i = 0; i < text.length; i++) {
    const unit = text.charCodeAt(i);
    const c = text[i];
    if (c === '"' || c === '\\' || c === '/') {
      out += '\\' + c;
    } else if (unit >= 0x20 && unit <= 0x7e) {
      out += c;
    } else {
      out += '\\u' + unit.toString(16).toUpperCase().padStart(4, '0');
    }
  }
  return out + '"';
}

const strings = [];
for (let unit = 0; unit <= 0x1f; unit++) {
  strings.push(String.fromCharCode(unit));
}
strings.push('"', '\\', '/', '\u007f', ' ', ' ', "<>&'", 'é', '\u{1F600}');
strings.push('\ud800', '\udc00', '\udc00\ud800', 'a\ud800', '\ud800a');

function bits(value) {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, value);
  return view.getBigUint64(0).toString(16).padStart(16, '0');
}

const numbers = [
  0, -0, 1, -1, 1.5, 0.1, 0.1 + 0.2, 1 / 3, 100, 1e20, 1e21, 123456789012345680000, 1e-6, 1e-7, 1.5e-7, -1e-7,
  123e-20, 2 ** 53, 2 ** 53 + 2, 1e300, 5e-324, 1.7976931348623157e308, NaN, Infinity, -Infinity,
];

const structures = [
  String.raw`{"a":[1,null,{"b":"c"}],"e":{},"f":[]}`,
  String.raw`{"k\"\n\ud800":[true,false,null]}`,
];

const vectors = {
  strings: strings.map((text) => {
    const json = literal(text);
    return { json, expected: JSON.stringify(JSON.parse(json)) };
  }),
  numbers: numbers.map((value) => ({ bits: bits(value), expected: JSON.stringify(value) })),
  structures: structures.map((json) => ({ json, expected: JSON.stringify(JSON.parse(json)) })),
};

writeFileSync(new URL('./stringify-vectors.json', import.meta.url), JSON.stringify(vectors, null, 2) + '\n');
