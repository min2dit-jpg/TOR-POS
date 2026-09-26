'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function hashFile(file) {
  const h = crypto.createHash('sha256');
  const fd = fs.openSync(file, 'r');
  const b = Buffer.alloc(65536);
  try {
    let n;
    while ((n = fs.readSync(fd, b, 0, b.length, null)) > 0) h.update(b.subarray(0, n));
    return h.digest('hex').toUpperCase();
  } finally {
    fs.closeSync(fd);
  }
}

function locked(dir, action) {
  fs.mkdirSync(dir, { recursive: true });
  const name = path.join(dir, '.publish.lock');
  let fd;
  try {
    fd = fs.openSync(name, 'wx');
  } catch {
    throw Error('Veröffentlichung gesperrt. Laufenden Vorgang prüfen; verwaiste .publish.lock erst nach Prüfung entfernen.');
  }

  try {
    return action();
  } finally {
    fs.closeSync(fd);
    fs.unlinkSync(name);
  }
}

function writeManifest(dir, name, value) {
  const target = path.join(dir, name);
  const temp = path.join(dir, `.manifest-${crypto.randomUUID()}.tmp`);
  try {
    const fd = fs.openSync(temp, 'wx');
    try {
      fs.writeFileSync(fd, JSON.stringify(value, null, 2) + '\n');
      fs.fsyncSync(fd);
    } finally {
      fs.closeSync(fd);
    }
    fs.renameSync(temp, target);
  } finally {
    if (fs.existsSync(temp)) fs.unlinkSync(temp);
  }
}

function validatePublication(o) {
  if (!/^\d+\.\d+\.\d+\.\d+$/.test(o.version || '') || !String(o.revision || '').trim())
    throw Error('Version/Revision ungültig.');
  if (!/^[A-F0-9]{64}$/.test(o.sha256 || '') || !/^[A-F0-9]{40}$/.test(o.signer_thumbprint || ''))
    throw Error('Geprüfte SHA256 und Signer erforderlich.');
}

// C-2: since R182 each product has its own installer. An update published for
// one edition lands in manifest-<EDITION>.json under that product's setup name;
// without an edition the shared manifest.json (KIOSK/IMBISS, TOR-POS-Pro-Setup)
// stays exactly as before.
const EDITION_PRODUCTS = {
  KIOSK: 'TOR-Einzelhandel-Setup',
  IMBISS: 'TOR-Gastro-Setup',
  RESTAURANT: 'TOR-Restaurant-Setup'
};

function editionOf(value) {
  if (value == null || value === '') return null;
  const edition = String(value).trim().toUpperCase();
  if (!Object.hasOwn(EDITION_PRODUCTS, edition))
    throw Error('Edition muss KIOSK, IMBISS oder RESTAURANT sein.');
  return edition;
}

function manifestNameFor(edition) {
  return edition ? `manifest-${edition}.json` : 'manifest.json';
}

function publishFile(dir, o, { prefix, manifestName, demo = false, editions = ['KIOSK', 'IMBISS'] }) {
  validatePublication(o);

  return locked(dir, () => {
    const filename = `${prefix}-${o.sha256}.exe`;
    const target = path.join(dir, filename);
    const tmp = path.join(dir, `.setup-${crypto.randomUUID()}.tmp`);

    try {
      fs.copyFileSync(o.source, tmp, fs.constants.COPYFILE_EXCL);
      if (hashFile(tmp) !== o.sha256) throw Error('Setup nach Prüfung verändert.');

      const fd = fs.openSync(tmp, 'r+');
      try {
        fs.fsyncSync(fd);
      } finally {
        fs.closeSync(fd);
      }

      if (fs.existsSync(target)) {
        if (hashFile(target) !== o.sha256) throw Error('Vorhandenes Setup beschädigt.');
      } else {
        fs.renameSync(tmp, target);
      }

      const m = {
        enabled: true,
        version: o.version,
        revision: o.revision,
        published_at: new Date().toISOString(),
        editions,
        filename,
        sha256: o.sha256,
        signer_thumbprint: o.signer_thumbprint,
        release_notes: String(o.release_notes || '')
      };

      if (demo) {
        m.trial_days = 7;
      } else {
        m.mandatory = !!o.mandatory;
      }

      writeManifest(dir, manifestName, m);
      return m;
    } finally {
      if (fs.existsSync(tmp)) fs.unlinkSync(tmp);
    }
  });
}

// Internal step: Windows entrypoint must verify Authenticode on the staged bytes first.
function publishVerified(dir, o) {
  const edition = editionOf(o.edition);
  return publishFile(dir, o, edition
    ? { prefix: EDITION_PRODUCTS[edition], manifestName: manifestNameFor(edition), editions: [edition] }
    : { prefix: 'TOR-POS-Pro-Setup', manifestName: 'manifest.json' });
}

function publishTrialVerified(dir, o) {
  return publishFile(dir, o, {
    prefix: 'TOR-POS-Demo-Setup',
    manifestName: 'trial-manifest.json',
    demo: true
  });
}

function disableManifest(dir, name) {
  return locked(dir, () => {
    const file = path.join(dir, name);
    const m = JSON.parse(fs.readFileSync(file, 'utf8'));
    m.enabled = false;
    writeManifest(dir, name, m);
    return m;
  });
}

function disable(dir, edition) {
  return disableManifest(dir, manifestNameFor(editionOf(edition)));
}

function disableTrial(dir) {
  return disableManifest(dir, 'trial-manifest.json');
}

module.exports = {
  EDITION_PRODUCTS,
  manifestNameFor,
  publishVerified,
  publishTrialVerified,
  disable,
  disableTrial,
  hashFile
};

if (require.main === module) {
  try {
    const [action, dir, input, edition] = process.argv.slice(2);
    if (action === 'publish') {
      publishVerified(dir, JSON.parse(fs.readFileSync(input, 'utf8').replace(/^\uFEFF/, '')));
    } else if (action === 'publish-trial') {
      publishTrialVerified(dir, JSON.parse(fs.readFileSync(input, 'utf8').replace(/^\uFEFF/, '')));
    } else if (action === 'disable') {
      disable(dir, input || edition);
    } else if (action === 'disable-trial') {
      disableTrial(dir);
    } else {
      throw Error('Ungültige Aktion.');
    }
  } catch (e) {
    console.error(e.message);
    process.exitCode = 1;
  }
}
