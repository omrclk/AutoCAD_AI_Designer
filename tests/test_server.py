import json, os, sys, threading, unittest
from unittest.mock import patch
from urllib.request import Request, urlopen
from urllib.error import HTTPError
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'service'))
import server as m

CTX = {'selected': [{'handle': 'A1'}], 'layers': [{'name': '0'}], 'blocks': []}


class PlanTests(unittest.TestCase):
    def setUp(self):
        m._history.clear()

    def test_without_key(self):
        with patch.dict('os.environ', {'OPENAI_API_KEY': ''}):
            self.assertEqual(m.plan('hi', CTX)['actions'], [])

    def test_valid_plan_and_history(self):
        good = json.dumps({'reply': 'ok', 'actions': [{'type': 'move', 'handles': ['a1'], 'dx': 10}]})
        with patch.dict('os.environ', {'OPENAI_API_KEY': 'k'}), patch.object(m, 'call_llm', return_value=good) as c:
            r = m.plan('taşı', CTX, 's1')
            self.assertEqual(r['actions'][0]['handles'], ['A1'])
            m.plan('bir daha', CTX, 's1')
            msgs = c.call_args[0][0]
            self.assertGreaterEqual(len(msgs), 4)  # system + history + user

    def test_invalid_then_retry_succeeds(self):
        bad = json.dumps({'reply': 'x', 'actions': [{'type': 'format_disk', 'handles': ['A1']}]})
        good = json.dumps({'reply': 'ok', 'actions': []})
        with patch.dict('os.environ', {'OPENAI_API_KEY': 'k'}), patch.object(m, 'call_llm', side_effect=[bad, good]):
            self.assertEqual(m.plan('sil', CTX)['reply'], 'ok')

    def test_invalid_twice_gives_empty_plan(self):
        with patch.dict('os.environ', {'OPENAI_API_KEY': 'k'}), patch.object(m, 'call_llm', return_value='not json'):
            self.assertEqual(m.plan('x', CTX)['actions'], [])

    def test_system_policy(self):
        self.assertIn('place_block_along', m.SYSTEM)
        self.assertIn('block_definitions', m.SYSTEM)
        self.assertIn('command', m.SYSTEM)


class HttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        m.TOKEN = 'tok'
        cls.srv = m.make_server(0)
        cls.port = cls.srv.server_address[1]
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def post(self, body, token='tok'):
        req = Request('http://127.0.0.1:%d/plan' % self.port, data=json.dumps(body).encode(),
                      headers={'X-CAD-AI-Token': token.encode('utf-8').decode('latin-1'), 'Content-Type': 'application/json'})
        try:
            with urlopen(req) as r:
                return r.status, json.load(r)
        except HTTPError as e:
            return e.code, json.load(e)

    def test_health(self):
        with urlopen('http://127.0.0.1:%d/health' % self.port) as r:
            self.assertTrue(json.load(r)['ok'])

    def test_auth(self):
        self.assertEqual(self.post({'message': 'x', 'context': {}}, token='bad')[0], 403)
        self.assertEqual(self.post({'message': 'x', 'context': {}}, token='türkçe')[0], 403)

    def test_bad_input(self):
        self.assertEqual(self.post({'message': '', 'context': {}})[0], 400)

    def test_plan_ok_and_upstream_error(self):
        with patch.object(m, 'plan', return_value={'reply': 'r', 'actions': []}):
            self.assertEqual(self.post({'message': 'x', 'context': {}})[0], 200)
        with patch.object(m, 'plan', side_effect=RuntimeError('API anahtarı geçersiz')):
            code, body = self.post({'message': 'x', 'context': {}})
            self.assertEqual(code, 502)
            self.assertIn('geçersiz', body['error'])


if __name__ == '__main__':
    unittest.main()

class TestYarimTRule(__import__('unittest').TestCase):
    def test_prompt_sizes_half_t_by_main(self):
        import server
        src = open(server.__file__, encoding='utf-8').read()
        self.assertIn('sized by the BRANCH tray width', src)
        self.assertNotIn('max(branch, main)', src)
