import { useEffect, useRef, useState, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';

export function useRealStream(sessionId, authToken) {
    const [sessionState, setSessionState] = useState('Stopped');
    const [latency, setLatency] = useState(0);
    const [isConnected, setIsConnected] = useState(false);
    const connectionRef = useRef(null);

    const connectHub = useCallback(async () => {
        if (!sessionId) return;

        const hubUrl = `${import.meta.env.VITE_API_BASE_URL || 'http://localhost:5000'}/hubs/cloudStream`;

        const connection = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl, {
                accessTokenFactory: () => authToken || localStorage.getItem('token')
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        connectionRef.current = connection;

        connection.on("ReceiveSessionUpdate", (data) => {
            if (data.sessionId === sessionId) {
                setSessionState(data.status);
                setLatency(data.latencyMs);
            }
        });

        connection.onreconnecting((error) => {
            console.warn(`[SignalR] Connection lost. Attempting reconnection...`, error);
            setIsConnected(false);
        });

        connection.onreconnected((connectionId) => {
            console.info(`[SignalR] Reconnected. Connection ID: ${connectionId}`);
            setIsConnected(true);
            connection.invoke("JoinSession", sessionId).catch(err => console.error("Failed to rejoin session:", err));
        });

        connection.onclose((error) => {
            console.error(`[SignalR] Connection closed.`, error);
            setIsConnected(false);
        });

        try {
            await connection.start();
            console.info("[SignalR] Connected successfully");
            setIsConnected(true);

            await connection.invoke("JoinSession", sessionId);
        } catch (err) {
            console.error("[SignalR] Connection failed:", err);
            setIsConnected(false);
            setTimeout(connectHub, 5000);
        }
    }, [sessionId, authToken]);

    useEffect(() => {
        connectHub();

        return () => {
            if (connectionRef.current) {
                connectionRef.current.invoke("LeaveSession", sessionId).catch(() => {});
                connectionRef.current.stop();
            }
        };
    }, [connectHub, sessionId]);

    return { sessionState, latency, isConnected };
}
