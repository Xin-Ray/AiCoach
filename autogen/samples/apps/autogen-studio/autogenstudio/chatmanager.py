import asyncio
import json
import os
import time
from datetime import datetime
from queue import Queue
from typing import Any, Dict, List, Optional, Tuple, Union

import websockets
from fastapi import WebSocket, WebSocketDisconnect

from .datamodel import Message, SocketMessage, Workflow
from .utils import (
    extract_successful_code_blocks,
    get_modified_files,
    summarize_chat_history,
)
from .workflowmanager import WorkflowManager


class AutoGenChatManager:
    """
    This class handles the automated generation and management of chat interactions
    using an automated workflow configuration and message queue.
    """

    def __init__(self, message_queue: Queue) -> None:
        """
        Initializes the AutoGenChatManager with a message queue.

        :param message_queue: A queue to use for sending messages asynchronously.
        """
        self.message_queue = message_queue

    def send(self, message: str) -> None:
        """
        Sends a message by putting it into the message queue.

        :param message: The message string to be sent.
        """
        if self.message_queue is not None:
            self.message_queue.put_nowait(message)

    def chat(
        self,
        message: Message,
        history: List[Dict[str, Any]],
        workflow: Any = None,
        connection_id: Optional[str] = None,
        user_dir: Optional[str] = None,
        **kwargs,
    ) -> Message:
        """
        Processes an incoming message according to the agent's workflow configuration
        and generates a response.

        :param message: An instance of `Message` representing an incoming message.
        :param history: A list of dictionaries, each representing a past interaction.
        :param flow_config: An instance of `AgentWorkFlowConfig`. If None, defaults to a standard configuration.
        :param connection_id: An optional connection identifier.
        :param kwargs: Additional keyword arguments.
        :return: An instance of `Message` representing a response.
        """

        # create a working director for workflow based on user_dir/session_id/time_hash
        work_dir = os.path.join(
            user_dir,
            str(message.session_id),
            datetime.now().strftime("%Y%m%d_%H-%M-%S"),
        )
        os.makedirs(work_dir, exist_ok=True)

        # if no flow config is provided, use the default
        if workflow is None:
            raise ValueError("Workflow must be specified")

        workflow_manager = WorkflowManager(
            workflow=workflow,
            history=history,
            work_dir=work_dir,
            send_message_function=self.send,
            connection_id=connection_id,
        )

        workflow = Workflow.model_validate(workflow)

        message_text = message.content.strip()

        start_time = time.time()
        workflow_manager.run(message=f"{message_text}", clear_history=False)
        end_time = time.time()

        metadata = {
            "messages": workflow_manager.agent_history,
            "summary_method": workflow.summary_method,
            "time": end_time - start_time,
            "files": get_modified_files(start_time, end_time, source_dir=work_dir),
        }

        output = self._generate_output(message_text, workflow_manager, workflow)

        output_message = Message(
            user_id=message.user_id,
            role="assistant",
            content=output,
            meta=json.dumps(metadata),
            session_id=message.session_id,
        )

        return output_message

    def _generate_output(
        self,
        message_text: str,
        workflow_manager: WorkflowManager,
        workflow: Workflow,
    ) -> str:
        """
        Generates the output response based on the workflow configuration and agent history.

        :param message_text: The text of the incoming message.
        :param flow: An instance of `WorkflowManager`.
        :param flow_config: An instance of `AgentWorkFlowConfig`.
        :return: The output response as a string.
        """

        output = ""
        if workflow.summary_method == "last":
            successful_code_blocks = extract_successful_code_blocks(workflow_manager.agent_history)
            last_message = (
                workflow_manager.agent_history[-1]["message"]["content"] if workflow_manager.agent_history else ""
            )
            successful_code_blocks = "\n\n".join(successful_code_blocks)
            output = (last_message + "\n" + successful_code_blocks) if successful_code_blocks else last_message
        elif workflow.summary_method == "llm":
            client = workflow_manager.receiver.client
            status_message = SocketMessage(
                type="agent_status",
                data={
                    "status": "summarizing",
                    "message": "Summarizing agent dialogue",
                },
                connection_id=workflow_manager.connection_id,
            )
            self.send(status_message.dict())
            output = summarize_chat_history(
                task=message_text,
                messages=workflow_manager.agent_history,
                client=client,
            )

        elif workflow.summary_method == "none":
            output = ""
        return output


class WebSocketConnectionManager:
    """
    Manages WebSocket connections including sending, broadcasting, and managing the lifecycle of connections.
    """

    def __init__(
        self,
        active_connections: List[Tuple[WebSocket, str]] = None,
        active_connections_lock: asyncio.Lock = None,
    ) -> None:
        """
        Initializes WebSocketConnectionManager with an optional list of active WebSocket connections.

        :param active_connections: A list of tuples, each containing a WebSocket object and its corresponding client_id.
        """
        if active_connections is None:
            active_connections = []
        self.active_connections_lock = active_connections_lock
        self.active_connections: List[Tuple[WebSocket, str]] = active_connections

    async def connect(self, websocket: WebSocket, client_id: str) -> None:
        """
        Accepts a new WebSocket connection and appends it to the active connections list.

        :param websocket: The WebSocket instance representing a client connection.
        :param client_id: A string representing the unique identifier of the client.
        """
        await websocket.accept()
        async with self.active_connections_lock:
            self.active_connections.append((websocket, client_id))
            print(f"New Connection: {client_id}, Total: {len(self.active_connections)}")

    async def disconnect(self, websocket: WebSocket) -> None:
        """
        Disconnects and removes a WebSocket connection from the active connections list.

        :param websocket: The WebSocket instance to remove.
        """
        async with self.active_connections_lock:
            try:
                self.active_connections = [conn for conn in self.active_connections if conn[0] != websocket]
                print(f"Connection Closed. Total: {len(self.active_connections)}")
            except ValueError:
                print("Error: WebSocket connection not found")

    async def disconnect_all(self) -> None:
        """
        Disconnects all active WebSocket connections.
        """
        for connection, _ in self.active_connections[:]:
            await self.disconnect(connection)

    async def send_message(self, message: Union[Dict, str], websocket: WebSocket) -> None:
        """
        Sends a JSON message to a single WebSocket connection.

        :param message: A JSON serializable dictionary containing the message to send.
        :param websocket: The WebSocket instance through which to send the message.
        """
        try:
            async with self.active_connections_lock:
                await websocket.send_json(message)
        except WebSocketDisconnect:
            print("Error: Tried to send a message to a closed WebSocket")
            await self.disconnect(websocket)
        except websockets.exceptions.ConnectionClosedOK:
            print("Error: WebSocket connection closed normally")
            await self.disconnect(websocket)
        except Exception as e:
            print(f"Error in sending message: {str(e)}", message)
            await self.disconnect(websocket)

    async def broadcast(self, message: Dict) -> None:
        """
        Broadcasts a JSON message to all active WebSocket connections.

        :param message: A JSON serializable dictionary containing the message to broadcast.
        """
        # Create a message dictionary with the desired format
        message_dict = {"message": message}

        for connection, _ in self.active_connections[:]:
            try:
                if connection.client_state == websockets.protocol.State.OPEN:
                    # Call send_message method with the message dictionary and current WebSocket connection
                    await self.send_message(message_dict, connection)
                else:
                    print("Error: WebSocket connection is closed")
                    await self.disconnect(connection)
            except (WebSocketDisconnect, websockets.exceptions.ConnectionClosedOK) as e:
                print(f"Error: WebSocket disconnected or closed({str(e)})")
                await self.disconnect(connection)
