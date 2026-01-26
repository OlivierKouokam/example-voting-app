const express = require('express');
const { Pool } = require('pg');
const cookieParser = require('cookie-parser');
const http = require('http');
const socketIO = require('socket.io');
const path = require('path');

const app = express();
const server = http.Server(app);
const io = socketIO(server);

/* =========================
   Configuration (ENV VARS)
   ========================= */

const port = process.env.PORT || 4000;

const pgHost = process.env.POSTGRES_HOST || 'postgres';
const pgPort = parseInt(process.env.POSTGRES_PORT || '5432');
const pgUser = process.env.POSTGRES_USER || 'postgres';
const pgPassword = process.env.POSTGRES_PASSWORD || 'postgres';
const pgDatabase = process.env.POSTGRES_DB || 'postgres';

/* =========================
   PostgreSQL Pool
   ========================= */

const pool = new Pool({
  host: pgHost,
  port: pgPort,
  user: pgUser,
  password: pgPassword,
  database: pgDatabase,
});

/* =========================
   Socket.IO
   ========================= */

io.on('connection', (socket) => {
  socket.emit('message', { text: 'Welcome!' });

  socket.on('subscribe', (data) => {
    socket.join(data.channel);
  });
});

/* =========================
   DB bootstrap / retry
   ========================= */

async function waitForDb() {
  while (true) {
    try {
      await pool.query('SELECT 1');
      console.log('Connected to PostgreSQL');
      break;
    } catch (err) {
      console.log('Waiting for PostgreSQL...');
      await new Promise(r => setTimeout(r, 1000));
    }
  }
}

/* =========================
   Votes polling
   ========================= */

async function getVotes() {
  try {
    const result = await pool.query(
      'SELECT vote, COUNT(id) AS count FROM votes GROUP BY vote'
    );

    const votes = collectVotesFromResult(result);
    io.sockets.emit('scores', JSON.stringify(votes));
  } catch (err) {
    console.error('Error querying votes:', err.message);
  } finally {
    setTimeout(getVotes, 1000);
  }
}

function collectVotesFromResult(result) {
  const votes = { a: 0, b: 0 };

  result.rows.forEach(row => {
    votes[row.vote] = parseInt(row.count, 10);
  });

  return votes;
}

/* =========================
   Express config
   ========================= */

app.use(cookieParser());
app.use(express.urlencoded({ extended: true }));
app.use(express.static(path.join(__dirname, 'views')));

app.get('/', (req, res) => {
  res.sendFile(path.join(__dirname, 'views', 'index.html'));
});

/* =========================
   Start server
   ========================= */

(async () => {
  await waitForDb();
  getVotes();

  server.listen(port, () => {
    console.log(`Result app listening on port ${port}`);
  });
})();

