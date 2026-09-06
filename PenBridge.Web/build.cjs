const fs = require('node:fs');
const path = require('node:path');
const root = __dirname;
const normalizeLineEndings = value => value.replace(/\r\n?/g, '\n');
const html = normalizeLineEndings(fs.readFileSync(path.join(root, 'pad.html'), 'utf8'));
fs.mkdirSync(path.join(root, 'dist'), { recursive: true });
fs.writeFileSync(path.join(root, 'dist', 'pad-loader.js'),
  `document.open();document.write(${JSON.stringify(html)});document.close();\n`);
const landing = normalizeLineEndings(fs.readFileSync(path.join(root, 'index.html'), 'utf8'))
  .replace("new URL('./dist/',location.href)", "new URL('./',location.href)");
fs.writeFileSync(path.join(root, 'dist', 'index.html'), landing);
console.log('Built PenBridge.Web/dist');
