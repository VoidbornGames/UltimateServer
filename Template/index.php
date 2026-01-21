
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>My Awesome Site</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: "Poppins", sans-serif;
            background: linear-gradient(135deg, #1e1e2f, #2e2e47);
            color: #fff;
            display: flex;
            flex-direction: column;
            min-height: 100vh;
            overflow-x: hidden;
        }
        header {
            background: rgba(255,255,255,0.05);
            backdrop-filter: blur(10px);
            padding: 1.5rem 3rem;
            text-align: center;
            border-bottom: 1px solid rgba(255,255,255,0.1);
        }
        header h1 {
            font-size: 2.2rem;
            letter-spacing: 1px;
        }
        header p {
            color: #a8a8c0;
            margin-top: .5rem;
            font-size: 1.1rem;
        }
        main {
            flex: 1;
            display: flex;
            align-items: center;
            justify-content: center;
            text-align: center;
            padding: 3rem 2rem;
        }
        main h2 {
            font-size: 2.5rem;
            background: linear-gradient(90deg, #ff416c, #ff4b2b);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            margin-bottom: 1rem;
        }
        main p {
            max-width: 600px;
            margin: 0 auto 2rem;
            color: #ccc;
            font-size: 1.1rem;
        }
        .button {
            display: inline-block;
            background: linear-gradient(90deg, #ff4b2b, #ff416c);
            color: white;
            padding: 0.9rem 1.8rem;
            border-radius: 50px;
            text-decoration: none;
            font-weight: 600;
            transition: 0.3s;
        }
        .button:hover {
            transform: scale(1.05);
            background: linear-gradient(90deg, #ff416c, #ff4b2b);
        }
        footer {
            text-align: center;
            padding: 1rem;
            font-size: 0.9rem;
            background: rgba(255,255,255,0.05);
            border-top: 1px solid rgba(255,255,255,0.1);
        }
        footer a {
            color: #ff416c;
            text-decoration: none;
        }
    </style>
</head>
<body>
    <header>
        <h1>My Awesome Site</h1>
        <p>Powered by UltimateServer SitePress</p>
    </header>

    <main>
        <div>
            <h2>Welcome to Your New Site 🎉</h2>
            <p>This page was automatically created for you by <strong>UltimateServer SitePress</strong>. 
               Edit this page in <code>index.php</code> or start building your site right away.</p>
            <a href="#" class="button">Get Started</a>
        </div>
    </main>

    <footer>
        <p>© 2025 My Awesome Site · Built with <a href="https://voidgames.ir/">UltimateServer SitePress</a></p>
    </footer>
</body>
</html>
