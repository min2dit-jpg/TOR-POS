'use strict';

// R125: password and token hashing, shared by server.js and tools/provision.js.
// The provisioning tool writes owner passwords and device tokens that the
// server later verifies; a second hand-written copy of these two functions is
// exactly how the two could silently stop agreeing (the lesson TOR POS learned
// with its VAT formula in R106).

const crypto = require('node:crypto');

function hashPassword(password, salt = crypto.randomBytes(16).toString('hex')) {
  const hash = crypto.scryptSync(password, salt, 64).toString('hex');
  return { salt, hash };
}

function hashToken(token) {
  return crypto.createHash('sha256').update(String(token)).digest('hex');
}

module.exports = { hashPassword, hashToken };
