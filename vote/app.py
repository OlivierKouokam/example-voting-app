from flask import Flask, render_template, request, make_response, g
from redis import Redis
import os
import socket
import random
import json
import logging

# Options par défaut
option_a = os.getenv('OPTION_A', "Cats")
option_b = os.getenv('OPTION_B', "Dogs")

# Nom de l'hôte
hostname = socket.gethostname()

# Création de l'application Flask
app = Flask(__name__)

# Configuration du logger Gunicorn
gunicorn_error_logger = logging.getLogger('gunicorn.error')
app.logger.handlers.extend(gunicorn_error_logger.handlers)
app.logger.setLevel(logging.INFO)


def get_redis():
    """Retourne une instance Redis stockée dans g pour réutilisation"""
    if not hasattr(g, 'redis'):
        g.redis = Redis(
            host=os.environ.get("REDIS_HOST", "redis"),
            port=int(os.environ.get("REDIS_PORT", 6379)),
            db=0,
            socket_timeout=5
        )
    return g.redis


@app.route("/", methods=['GET', 'POST'])
def hello():
    # Récupération ou création du voter_id
    voter_id = request.cookies.get('voter_id')
    if not voter_id:
        voter_id = hex(random.getrandbits(64))[2:-1]

    vote = None

    # Traitement d'un vote POST
    if request.method == 'POST':
        redis = get_redis()
        vote = request.form.get('vote')
        if vote:
            app.logger.info('Received vote for %s', vote)
            data = json.dumps({'voter_id': voter_id, 'vote': vote})
            redis.rpush('votes', data)

    # Construction de la réponse
    resp = make_response(
        render_template(
            'index.html',
            option_a=option_a,
            option_b=option_b,
            hostname=hostname,
            vote=vote
        )
    )
    resp.set_cookie('voter_id', voter_id)
    return resp


if __name__ == "__main__":
    app.run(host='0.0.0.0', port=80, debug=True, threaded=True)

