const fs = require('node:fs');
const path = require('node:path');
const root = __dirname;
const html = fs.readFileSync(path.join(root, 'pad.html'), 'utf8');
fs.mkdirSync(path.join(root, 'dist'), { recursive: true });
fs.writeFileSync(path.join(root, 'dist', 'pad-loader.js'),
  `document.open();document.write(${JSON.stringify(html)});document.close();\n`);
fs.copyFileSync(path.join(root, 'index.html'), path.join(root, 'dist', 'index.html'));
console.log('Built PenBridge.Web/dist');
