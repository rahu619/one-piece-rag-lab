#!/bin/bash
# Start Ollama in the background
/bin/ollama serve &
PID=$!

# Wait for Ollama to be ready (API should respond)
echo "Waiting for Ollama to start..."
while ! curl -s http://localhost:11434/api/tags > /dev/null; do
  sleep 2
done

# Pull the model
echo "Ensuring model qwen2.5-coder:1.5b is pulled..."
/bin/ollama pull qwen2.5-coder:1.5b

# Keep the container running
wait $PID